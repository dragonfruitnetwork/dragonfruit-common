﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System.Collections.Generic;
using System.Linq;
using DragonFruit.Data.Parameters;
using DragonFruit.Data.Utils;
using NUnit.Framework;

namespace DragonFruit.Data.Tests
{
    [TestFixture]
    public class RequestDataCompilationTests
    {
        [Test]
        public void TestQueries()
        {
            var query = new TestRequest().FullUrl.Split('?').Last().Split('&');

            for (var i = 0; i < TestRequest.TestDataset.Length; i++)
            {
                var testString = TestRequest.TestDataset[i];
                Assert.IsTrue(query.Contains($"{TestRequest.QueryName}={testString}"));
                Assert.IsTrue(query.Contains($"{TestRequest.QueryName}[]={testString}"));
                Assert.IsTrue(query.Contains($"{TestRequest.QueryName}[{i}]={testString}"));
            }

            Assert.IsTrue(query.Contains($"{TestRequest.QueryName}={string.Join(":", TestRequest.TestDataset)}"));
        }

        [Test]
        public void TestEnumHandling()
        {
            var request = new TestRequest();
            var query = request.FullUrl.Split('?').Last().Split('&');

            Assert.IsTrue(query.Contains($"enum={nameof(EnumValues.Red)}"));
            Assert.IsTrue(query.Contains($"enum={nameof(EnumValues.Blue).ToLower(CultureUtils.DefaultCulture)}"));
            Assert.IsTrue(query.Contains($"enum={(int)EnumValues.Green}"));
        }

        [Test]
        public void TestAdditionalQueryHandling()
        {
            var request = new TestRequest();
            var query = request.FullUrl.Split('?').Last().Split('&');

            Assert.IsTrue(query.Contains("a=x"));
        }
    }

    internal class TestRequest : ApiRequest
    {
        internal const string QueryName = "data";
        internal static readonly string[] TestDataset = { "a", "b", "c" };

        public override string Path => "http://example.com";

        protected override IEnumerable<KeyValuePair<string, string>> AdditionalQueries => new[]
        {
            new KeyValuePair<string, string>("a", "x")
        };

        [QueryParameter(QueryName, CollectionConversionMode.Recursive)]
        public string[] RecursiveData { get; set; } = TestDataset;

        [QueryParameter(QueryName, CollectionConversionMode.Ordered)]
        public string[] OrderedData { get; set; } = TestDataset;

        [QueryParameter(QueryName, CollectionConversionMode.Unordered)]
        public string[] UnorderedData { get; set; } = TestDataset;

        [QueryParameter(QueryName, CollectionConversionMode.Concatenated, CollectionSeparator = ":")]
        public string[] ConcatenatedData { get; set; } = TestDataset;

        [QueryParameter("enum", EnumHandlingMode.String)]
        public EnumValues StringEnum => EnumValues.Red;

        [QueryParameter("enum", EnumHandlingMode.StringLower)]
        public EnumValues SmallStringEnum => EnumValues.Blue;

        [QueryParameter("enum", EnumHandlingMode.Numeric)]
        public EnumValues NumericEnum => EnumValues.Green;
    }

    public enum EnumValues
    {
        Red,
        Blue,
        Green = 512
    }
}
