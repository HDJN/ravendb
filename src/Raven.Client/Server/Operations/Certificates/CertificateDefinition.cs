﻿using System;
using System.Collections.Generic;
using Sparrow.Json.Parsing;

namespace Raven.Client.Server.Operations.Certificates
{
    public class CertificateDefinition
    {
        public string Certificate;
        public bool ServerAdmin;
        public string Thumbprint;
        public HashSet<string> Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(Certificate)] = Certificate,
                [nameof(Thumbprint)] = Thumbprint,
                [nameof(ServerAdmin)] = ServerAdmin,
                [nameof(Permissions)] = Permissions
            };
        }
    }

    public class CertificateRawData
    {
        public byte[] RawData;
    }
}