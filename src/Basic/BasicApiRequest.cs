﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System;
using System.Collections.Generic;
using DragonFruit.Data.Utils;

namespace DragonFruit.Data.Basic
{
    public class BasicApiRequest : ApiRequest, IBasicApiRequest
    {
        /// <summary>
        /// Initialises a new <see cref="BasicApiRequest"/> with a path to the resource
        /// </summary>
        /// <param name="path"></param>
        public BasicApiRequest(string path)
        {
            Path = path;
        }

        public override string Path { get; }

        internal override string UrlCompiler => Queries.IsValueCreated
            ? Path + QueryUtils.QueryStringFrom(Queries.Value)
            : Path;

        /// <summary>
        /// Collection of <see cref="KeyValuePair{TKey,TValue}"/>s to use as a query string
        /// </summary>
        public Lazy<List<KeyValuePair<string, string>>> Queries { get; } = new Lazy<List<KeyValuePair<string, string>>>(() => new List<KeyValuePair<string, string>>());
    }
}
