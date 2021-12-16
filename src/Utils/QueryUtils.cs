﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System.Collections.Generic;
using System.Linq;

namespace DragonFruit.Data.Utils
{
    internal static class QueryUtils
    {
        /// <summary>
        /// Produces a query string from an <see cref="IEnumerable{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>s
        /// </summary>
        public static string QueryStringFrom(IEnumerable<KeyValuePair<string, string>> queries)
        {
            return !queries.Any()
                ? string.Empty
                : $"?{string.Join("&", queries.Select(kvp => $"{kvp.Key}={kvp.Value}"))}";
        }
    }
}
