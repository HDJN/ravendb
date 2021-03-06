﻿using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron.Debugging;

namespace Raven.Server.Documents.Handlers.Debugging
{
    public class StorageHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/debug/storage/report", "GET", AuthorizationStatus.ValidUser, IsDebugInformationEndpoint = true)]
        public Task Report()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();

                    writer.WritePropertyName("BasePath");
                    writer.WriteString(Database.Configuration.Core.DataDirectory.FullPath);
                    writer.WriteComma();

                    writer.WritePropertyName("Results");
                    writer.WriteStartArray();
                    var first = true;
                    foreach (var env in Database.GetAllStoragesEnvironment())
                    {
                        if (first == false)
                            writer.WriteComma();

                        first = false;

                        writer.WriteStartObject();

                        writer.WritePropertyName("Name");
                        writer.WriteString(env.Name);
                        writer.WriteComma();

                        writer.WritePropertyName("Type");
                        writer.WriteString(env.Type.ToString());
                        writer.WriteComma();

                        var djv = (DynamicJsonValue)TypeConverter.ToBlittableSupportedType(GetReport(env));
                        writer.WritePropertyName("Report");
                        writer.WriteObject(context.ReadObject(djv, env.Name));

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/debug/storage/all-environments/report", "GET", AuthorizationStatus.ValidUser, IsDebugInformationEndpoint = true)]
        public Task AllEnvironmentsReport()
        {
            var name = GetStringQueryString("database");

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();

                    writer.WritePropertyName("DatabaseName");
                    writer.WriteString(name);
                    writer.WriteComma();

                    writer.WritePropertyName("Environments");
                    writer.WriteStartArray();
                    WriteAllEnvs(writer, context);
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
            }

            return Task.CompletedTask;
        }

        private void WriteAllEnvs(BlittableJsonTextWriter writer, DocumentsOperationContext context)
        {
            var envs = Database.GetAllStoragesEnvironment();

            bool first = true;
            foreach (var env in envs)
            {
                if (env == null)
                    continue;

                if (!first)
                    writer.WriteComma();
                first = false;

                writer.WriteStartObject();
                writer.WritePropertyName("Environment");
                writer.WriteString(env.Name);
                writer.WriteComma();

                writer.WritePropertyName("Type");
                writer.WriteString(env.Type.ToString());
                writer.WriteComma();

                var djv = (DynamicJsonValue)TypeConverter.ToBlittableSupportedType(GetDetailedReport(env, false));
                writer.WritePropertyName("Report");
                writer.WriteObject(context.ReadObject(djv, env.Name));

                writer.WriteEndObject();
            }
        }


        [RavenAction("/databases/*/debug/storage/environment/report", "GET", AuthorizationStatus.ValidUser)]
        public Task EnvironmentReport()
        {
            var name = GetStringQueryString("name");
            var typeAsString = GetStringQueryString("type");
            var details = GetBoolValueQueryString("details", required: false) ?? false;

            if (Enum.TryParse(typeAsString, out StorageEnvironmentWithType.StorageEnvironmentType type) == false)
                throw new InvalidOperationException("Query string value 'type' is not a valid environment type: " + typeAsString);

            var env = Database.GetAllStoragesEnvironment()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.Type == type);

            if (env == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();

                    writer.WritePropertyName("Name");
                    writer.WriteString(env.Name);
                    writer.WriteComma();

                    writer.WritePropertyName("Type");
                    writer.WriteString(env.Type.ToString());
                    writer.WriteComma();

                    var djv = (DynamicJsonValue)TypeConverter.ToBlittableSupportedType(GetDetailedReport(env, details));
                    writer.WritePropertyName("Report");
                    writer.WriteObject(context.ReadObject(djv, env.Name));

                    writer.WriteEndObject();
                }
            }

            return Task.CompletedTask;
        }

        private static StorageReport GetReport(StorageEnvironmentWithType environment)
        {
            using (var tx = environment.Environment.ReadTransaction())
            {
                return environment.Environment.GenerateReport(tx);
            }
        }

        private DetailedStorageReport GetDetailedReport(StorageEnvironmentWithType environment, bool details)
        {
            if (environment.Type != StorageEnvironmentWithType.StorageEnvironmentType.Index)
            {
                using (var tx = environment.Environment.ReadTransaction())
                {
                    return environment.Environment.GenerateDetailedReport(tx, details);
                }
            }

            var index = Database.IndexStore.GetIndex(environment.Name);
            return index.GenerateStorageReport(details);
        }
    }
}
