﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.
namespace Microsoft.DbContextPackage
{
    using System;

    internal static class GuidList
    {
        public const string guidDbContextPackagePkgString = "f4c4712c-ceae-4803-8e52-0e2049d5de9f";
        public const string guidDbContextPackageCmdSetString = "c769a05d-8d51-4919-bfe6-5f35a0eaf27e";

        public static readonly Guid guidDbContextPackageCmdSet = new Guid(guidDbContextPackageCmdSetString);
    }
}