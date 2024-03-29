﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hyperledger.Aries.Contracts;
using Hyperledger.Aries.Decorators;
using Hyperledger.Aries.Decorators.Attachments;
using Hyperledger.Aries.Decorators.Threading;
using Hyperledger.Aries.Agents;
using Hyperledger.Aries.Extensions;
using Hyperledger.Aries.Models.Events;
using Hyperledger.Aries.Utils;
using Hyperledger.Indy.AnonCredsApi;
using Hyperledger.Indy.PoolApi;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Hyperledger.Aries.Features.DidExchange;
using Hyperledger.Aries.Features.IssueCredential;
using Hyperledger.Aries.Configuration;
using Hyperledger.Aries.Storage;
using Hyperledger.Aries.Decorators.Service;

namespace Hyperledger.Aries.Features.PresentProof
{
    /// <summary>
    /// Proof Service
    /// </summary>
    /// <seealso cref="IProofService" />
    public class DefaultProofService : IProofService
    {
        /// <summary>
        /// The event aggregator
        /// </summary>
        protected readonly IEventAggregator EventAggregator;

        /// <summary>
        /// The connection service
        /// </summary>
        protected readonly IConnectionService ConnectionService;

        /// <summary>
        /// The record service
        /// </summary>
        protected readonly IWalletRecordService RecordService;

        /// <summary>
        /// The provisioning service
        /// </summary>
        protected readonly IProvisioningService ProvisioningService;

        /// <summary>
        /// The ledger service
        /// </summary>
        protected readonly ILedgerService LedgerService;

        /// <summary>
        /// The logger
        /// </summary>
        protected readonly ILogger<DefaultProofService> Logger;

        /// <summary>
        /// The tails service
        /// </summary>
        protected readonly ITailsService TailsService;

        /// <summary>
        /// Message Service
        /// </summary>
        protected readonly IMessageService MessageService;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultProofService"/> class.
        /// </summary>
        /// <param name="eventAggregator">The event aggregator.</param>
        /// <param name="connectionService">The connection service.</param>
        /// <param name="recordService">The record service.</param>
        /// <param name="provisioningService">The provisioning service.</param>
        /// <param name="ledgerService">The ledger service.</param>
        /// <param name="tailsService">The tails service.</param>
        /// <param name="messageService">The message service.</param>
        /// <param name="logger">The logger.</param>
        public DefaultProofService(
            IEventAggregator eventAggregator,
            IConnectionService connectionService,
            IWalletRecordService recordService,
            IProvisioningService provisioningService,
            ILedgerService ledgerService,
            ITailsService tailsService,
            IMessageService messageService,
            ILogger<DefaultProofService> logger)
        {
            EventAggregator = eventAggregator;
            TailsService = tailsService;
            MessageService = messageService;
            ConnectionService = connectionService;
            RecordService = recordService;
            ProvisioningService = provisioningService;
            LedgerService = ledgerService;
            Logger = logger;
        }

        /// <inheritdoc />
        public virtual async Task<string> CreateProofAsync(IAgentContext agentContext,
            ProofRequest proofRequest, RequestedCredentials requestedCredentials)
        {
            var provisioningRecord = await ProvisioningService.GetProvisioningAsync(agentContext.Wallet);

            var credentialObjects = new List<CredentialInfo>();
            foreach (var credId in requestedCredentials.GetCredentialIdentifiers())
            {
                credentialObjects.Add(
                    JsonConvert.DeserializeObject<CredentialInfo>(
                        await AnonCreds.ProverGetCredentialAsync(agentContext.Wallet, credId)));
            }

            var schemas = await BuildSchemasAsync(await agentContext.Pool,
                credentialObjects
                    .Select(x => x.SchemaId)
                    .Distinct());

            var definitions = await BuildCredentialDefinitionsAsync(await agentContext.Pool,
                credentialObjects
                    .Select(x => x.CredentialDefinitionId)
                    .Distinct());

            var revocationStates = await BuildRevocationStatesAsync(await agentContext.Pool,
                credentialObjects,
                requestedCredentials);

            var proofJson = await AnonCreds.ProverCreateProofAsync(agentContext.Wallet, proofRequest.ToJson(),
                requestedCredentials.ToJson(), provisioningRecord.MasterSecretId, schemas, definitions,
                revocationStates);

            return proofJson;
        }

        /// <inheritdoc />
        public async Task<ProofRecord> CreatePresentationAsync(IAgentContext agentContext, RequestPresentationMessage requestPresentation, RequestedCredentials requestedCredentials)
        {
            var service = requestPresentation.GetDecorator<ServiceDecorator>(DecoratorNames.ServiceDecorator);

            var record = await ProcessRequestAsync(agentContext, requestPresentation, null);
            var (presentationMessage, proofRecord) = await CreatePresentationAsync(agentContext, record.Id, requestedCredentials);

            await MessageService.SendAsync(
                wallet: agentContext.Wallet,
                message: presentationMessage,
                recipientKey: service.RecipientKeys.First(),
                endpointUri: service.ServiceEndpoint,
                routingKeys: service.RoutingKeys.ToArray());

            return proofRecord;
        }

        /// <inheritdoc />
        public virtual async Task RejectProofRequestAsync(IAgentContext agentContext, string proofRequestId)
        {
            var request = await GetAsync(agentContext, proofRequestId);

            if (request.State != ProofState.Requested)
                throw new AriesFrameworkException(ErrorCode.RecordInInvalidState,
                    $"Proof record state was invalid. Expected '{ProofState.Requested}', found '{request.State}'");

            await request.TriggerAsync(ProofTrigger.Reject);
            await RecordService.UpdateAsync(agentContext.Wallet, request);
        }

        /// <inheritdoc />
        public virtual async Task<bool> VerifyProofAsync(IAgentContext agentContext, string proofRequestJson, string proofJson)
        {
            var proof = JsonConvert.DeserializeObject<PartialProof>(proofJson);

            var schemas = await BuildSchemasAsync(await agentContext.Pool,
                proof.Identifiers
                    .Select(x => x.SchemaId)
                    .Where(x => x != null)
                    .Distinct());

            var definitions = await BuildCredentialDefinitionsAsync(await agentContext.Pool,
                proof.Identifiers
                    .Select(x => x.CredentialDefintionId)
                    .Where(x => x != null)
                    .Distinct());

            var revocationDefinitions = await BuildRevocationRegistryDefinitionsAsync(await agentContext.Pool,
                proof.Identifiers
                    .Select(x => x.RevocationRegistryId)
                    .Where(x => x != null)
                    .Distinct());

            var revocationRegistries = await BuildRevocationRegistryDetlasAsync(await agentContext.Pool,
                proof.Identifiers
                    .Where(x => x.RevocationRegistryId != null));

            return await AnonCreds.VerifierVerifyProofAsync(proofRequestJson, proofJson, schemas,
                definitions, revocationDefinitions, revocationRegistries);
        }

        /// <inheritdoc />
        public virtual async Task<bool> VerifyProofAsync(IAgentContext agentContext, string proofRecId)
        {
            var proofRecord = await GetAsync(agentContext, proofRecId);

            if (proofRecord.State != ProofState.Accepted)
                throw new AriesFrameworkException(ErrorCode.RecordInInvalidState,
                    $"Proof record state was invalid. Expected '{ProofState.Accepted}', found '{proofRecord.State}'");

            return await VerifyProofAsync(agentContext, proofRecord.RequestJson, proofRecord.ProofJson);
        }

        /// <inheritdoc />
        public virtual Task<List<ProofRecord>> ListAsync(IAgentContext agentContext, ISearchQuery query = null,
            int count = 100) => RecordService.SearchAsync<ProofRecord>(agentContext.Wallet, query, null, count);

        /// <inheritdoc />
        public virtual async Task<ProofRecord> GetAsync(IAgentContext agentContext, string proofRecId)
        {
            Logger.LogInformation(LoggingEvents.GetProofRecord, "ProofRecordId {0}", proofRecId);

            return await RecordService.GetAsync<ProofRecord>(agentContext.Wallet, proofRecId) ??
                   throw new AriesFrameworkException(ErrorCode.RecordNotFound, "Proof record not found");
        }

        /// <inheritdoc />
        public virtual async Task<List<Credential>> ListCredentialsForProofRequestAsync(IAgentContext agentContext,
            ProofRequest proofRequest, string attributeReferent)
        {
            using (var search =
                await AnonCreds.ProverSearchCredentialsForProofRequestAsync(agentContext.Wallet, proofRequest.ToJson()))
            {
                var searchResult = await search.NextAsync(attributeReferent, 100);
                return JsonConvert.DeserializeObject<List<Credential>>(searchResult);
            }
        }

        #region Private Methods

        private async Task<string> BuildSchemasAsync(Pool pool, IEnumerable<string> schemaIds)
        {
            var result = new Dictionary<string, JObject>();

            foreach (var schemaId in schemaIds)
            {
                var ledgerSchema = await LedgerService.LookupSchemaAsync(pool, schemaId);
                result.Add(schemaId, JObject.Parse(ledgerSchema.ObjectJson));
            }

            return result.ToJson();
        }

        private async Task<string> BuildCredentialDefinitionsAsync(Pool pool, IEnumerable<string> credentialDefIds)
        {
            var result = new Dictionary<string, JObject>();

            foreach (var schemaId in credentialDefIds)
            {
                var ledgerDefinition = await LedgerService.LookupDefinitionAsync(pool, schemaId);
                result.Add(schemaId, JObject.Parse(ledgerDefinition.ObjectJson));
            }

            return result.ToJson();
        }

        private async Task<string> BuildRevocationStatesAsync(Pool pool,
            IEnumerable<CredentialInfo> credentialObjects,
            RequestedCredentials requestedCredentials)
        {
            var allCredentials = new List<RequestedAttribute>();
            allCredentials.AddRange(requestedCredentials.RequestedAttributes.Values);
            allCredentials.AddRange(requestedCredentials.RequestedPredicates.Values);

            var result = new Dictionary<string, Dictionary<string, JObject>>();
            foreach (var requestedCredential in allCredentials)
            {
                // ReSharper disable once PossibleMultipleEnumeration
                var credential = credentialObjects.First(x => x.Referent == requestedCredential.CredentialId);
                if (credential.RevocationRegistryId == null)
                    continue;

                var timestamp = requestedCredential.Timestamp ??
                                throw new Exception(
                                    "Timestamp must be provided for credential that supports revocation");

                if (result.ContainsKey(credential.RevocationRegistryId) &&
                    result[credential.RevocationRegistryId].ContainsKey($"{timestamp}"))
                {
                    continue;
                }

                var registryDefinition =
                    await LedgerService.LookupRevocationRegistryDefinitionAsync(pool,
                        credential.RevocationRegistryId);

                var delta = await LedgerService.LookupRevocationRegistryDeltaAsync(pool,
                    credential.RevocationRegistryId, -1, timestamp);

                var tailsfile = await TailsService.EnsureTailsExistsAsync(pool, credential.RevocationRegistryId);
                var tailsReader = await TailsService.OpenTailsAsync(tailsfile);

                var state = await AnonCreds.CreateRevocationStateAsync(tailsReader, registryDefinition.ObjectJson,
                    delta.ObjectJson, (long)delta.Timestamp, credential.CredentialRevocationId);

                if (!result.ContainsKey(credential.RevocationRegistryId))
                    result.Add(credential.RevocationRegistryId, new Dictionary<string, JObject>());

                result[credential.RevocationRegistryId].Add($"{timestamp}", JObject.Parse(state));

                // TODO: Revocation state should provide the state between a certain period
                // that can be requested in the proof request in the 'non_revocation' field.
            }

            return result.ToJson();
        }

        private async Task<string> BuildRevocationRegistryDetlasAsync(Pool pool,
            IEnumerable<ProofIdentifier> proofIdentifiers)
        {
            var result = new Dictionary<string, Dictionary<string, JObject>>();

            foreach (var identifier in proofIdentifiers)
            {
                var delta = await LedgerService.LookupRevocationRegistryDeltaAsync(pool,
                    identifier.RevocationRegistryId,
                    -1,
                    long.Parse(identifier.Timestamp));

                result.Add(identifier.RevocationRegistryId,
                    new Dictionary<string, JObject>
                    {
                        {identifier.Timestamp, JObject.Parse(delta.ObjectJson)}
                    });
            }

            return result.ToJson();
        }

        private async Task<string> BuildRevocationRegistryDefinitionsAsync(Pool pool,
            IEnumerable<string> revocationRegistryIds)
        {
            var result = new Dictionary<string, JObject>();

            foreach (var revocationRegistryId in revocationRegistryIds)
            {
                var ledgerSchema =
                    await LedgerService.LookupRevocationRegistryDefinitionAsync(pool, revocationRegistryId);
                result.Add(revocationRegistryId, JObject.Parse(ledgerSchema.ObjectJson));
            }

            return result.ToJson();
        }

        /// <inheritdoc />
        public Task<(RequestPresentationMessage, ProofRecord)> CreateRequestAsync(
            IAgentContext agentContext,
            ProofRequest proofRequest,
            string connectionId) =>
            CreateRequestAsync(
                agentContext: agentContext,
                proofRequestJson: proofRequest?.ToJson(),
                connectionId: connectionId);

        /// <inheritdoc />
        public async Task<(RequestPresentationMessage, ProofRecord)> CreateRequestAsync(IAgentContext agentContext, string proofRequestJson, string connectionId)
        {
            Logger.LogInformation(LoggingEvents.CreateProofRequest, "ConnectionId {0}", connectionId);

            if (proofRequestJson == null)
            {
                throw new ArgumentNullException(nameof(proofRequestJson), "You must provide proof request");
            }
            if (connectionId != null)
            {
                var connection = await ConnectionService.GetAsync(agentContext, connectionId);

                if (connection.State != ConnectionState.Connected)
                    throw new AriesFrameworkException(ErrorCode.RecordInInvalidState,
                        $"Connection state was invalid. Expected '{ConnectionState.Connected}', found '{connection.State}'");
            }

            var threadId = Guid.NewGuid().ToString();
            var proofRecord = new ProofRecord
            {
                Id = Guid.NewGuid().ToString(),
                ConnectionId = connectionId,
                RequestJson = proofRequestJson
            };
            proofRecord.SetTag(TagConstants.Role, TagConstants.Requestor);
            proofRecord.SetTag(TagConstants.LastThreadId, threadId);

            await RecordService.AddAsync(agentContext.Wallet, proofRecord);

            var message = new RequestPresentationMessage
            {
                Id = threadId,
                Requests = new[]
                {
                    new Attachment
                    {
                        Id = "libindy-request-presentation-0",
                        MimeType = CredentialMimeTypes.ApplicationJsonMimeType,
                        Data = new AttachmentContent
                        {
                            Base64 = proofRequestJson
                                .GetUTF8Bytes()
                                .ToBase64String()
                        }
                    }
                }
            };
            message.ThreadFrom(threadId);
            return (message, proofRecord);
        }

        /// <inheritdoc />
        public async Task<(RequestPresentationMessage, ProofRecord)> CreateRequestAsync(IAgentContext agentContext, ProofRequest proofRequest)
        {
            var (message, record) = await CreateRequestAsync(agentContext, proofRequest, null);
            var provisioning = await ProvisioningService.GetProvisioningAsync(agentContext.Wallet);

            message.AddDecorator(provisioning.ToServiceDecorator(), DecoratorNames.ServiceDecorator);
            record.SetTag("RequestData", message.ToByteArray().ToBase64UrlString());

            return (message, record);
        }

        /// <inheritdoc />
        public async Task<ProofRecord> ProcessRequestAsync(IAgentContext agentContext, RequestPresentationMessage requestPresentationMessage, ConnectionRecord connection)
        {
            var requestAttachment = requestPresentationMessage.Requests.FirstOrDefault(x => x.Id == "libindy-request-presentation-0")
                ?? throw new ArgumentException("Presentation request attachment not found.");

            var requestJson = requestAttachment.Data.Base64.GetBytesFromBase64().GetUTF8String();

            // Write offer record to local wallet
            var proofRecord = new ProofRecord
            {
                Id = Guid.NewGuid().ToString(),
                RequestJson = requestJson,
                ConnectionId = connection?.Id,
                State = ProofState.Requested
            };
            proofRecord.SetTag(TagConstants.LastThreadId, requestPresentationMessage.GetThreadId());
            proofRecord.SetTag(TagConstants.Role, TagConstants.Holder);

            await RecordService.AddAsync(agentContext.Wallet, proofRecord);

            EventAggregator.Publish(new ServiceMessageProcessingEvent
            {
                RecordId = proofRecord.Id,
                MessageType = requestPresentationMessage.Type,
                ThreadId = requestPresentationMessage.GetThreadId()
            });

            return proofRecord;
        }

        /// <inheritdoc />
        public async Task<ProofRecord> ProcessPresentationAsync(IAgentContext agentContext, PresentationMessage presentationMessage)
        {
            var proofRecord = await this.GetByThreadIdAsync(agentContext, presentationMessage.GetThreadId());

            var requestAttachment = presentationMessage.Presentations.FirstOrDefault(x => x.Id == "libindy-presentation-0")
                ?? throw new ArgumentException("Presentation attachment not found.");

            var proofJson = requestAttachment.Data.Base64.GetBytesFromBase64().GetUTF8String();

            if (proofRecord.State != ProofState.Requested)
                throw new AriesFrameworkException(ErrorCode.RecordInInvalidState,
                    $"Proof state was invalid. Expected '{ProofState.Requested}', found '{proofRecord.State}'");

            proofRecord.ProofJson = proofJson;
            await proofRecord.TriggerAsync(ProofTrigger.Accept);
            await RecordService.UpdateAsync(agentContext.Wallet, proofRecord);

            EventAggregator.Publish(new ServiceMessageProcessingEvent
            {
                RecordId = proofRecord.Id,
                MessageType = presentationMessage.Type,
                ThreadId = presentationMessage.GetThreadId()
            });

            return proofRecord;
        }

        /// <inheritdoc />
        public Task<string> CreatePresentationAsync(IAgentContext agentContext, ProofRequest proofRequest, RequestedCredentials requestedCredentials) =>
            CreateProofAsync(agentContext, proofRequest, requestedCredentials);

        /// <inheritdoc />
        public async Task<(PresentationMessage, ProofRecord)> CreatePresentationAsync(IAgentContext agentContext, string proofRecordId, RequestedCredentials requestedCredentials)
        {
            var record = await GetAsync(agentContext, proofRecordId);

            if (record.State != ProofState.Requested)
                throw new AriesFrameworkException(ErrorCode.RecordInInvalidState,
                    $"Proof state was invalid. Expected '{ProofState.Requested}', found '{record.State}'");

            var proofJson = await CreatePresentationAsync(
                agentContext,
                record.RequestJson.ToObject<ProofRequest>(),
                requestedCredentials);

            record.ProofJson = proofJson;
            await record.TriggerAsync(ProofTrigger.Accept);
            await RecordService.UpdateAsync(agentContext.Wallet, record);

            var threadId = record.GetTag(TagConstants.LastThreadId);

            var proofMsg = new PresentationMessage
            {
                Id = threadId,
                Presentations = new[]
                {
                    new Attachment
                    {
                        Id = "libindy-presentation-0",
                        MimeType = CredentialMimeTypes.ApplicationJsonMimeType,
                        Data = new AttachmentContent
                        {
                            Base64 = proofJson
                                .GetUTF8Bytes()
                                .ToBase64String()
                        }
                    }
                }
            };
            proofMsg.ThreadFrom(threadId);

            return (proofMsg, record);
        }

        #endregion
    }
}