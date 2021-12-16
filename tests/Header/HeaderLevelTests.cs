﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System;
using DragonFruit.Data.Extensions;
using DragonFruit.Data.Tests.Header.Objects;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DragonFruit.Data.Tests.Header
{
    [TestFixture]
    public class HeaderLevelTests : ApiTest
    {
        private const string HeaderName = "x-dfn-test";
        private const string GlobalHeaderName = "x-dfn-global";

        /// <summary>
        /// Test whether request-headers and default headers are sent together successfully
        /// </summary>
        [TestCase]
        public void LevelSpecificHeaderTest()
        {
            var request = new EchoRequest();

            var globalHeaderValue = Guid.NewGuid().ToString();
            var requestHeaderValue = Guid.NewGuid().ToString();

            Client.Headers[GlobalHeaderName] = globalHeaderValue;
            request.WithHeader(HeaderName, requestHeaderValue);

            var response = Client.Perform<JObject>(request);
            Assert.AreEqual(requestHeaderValue, (string)response["headers"]![HeaderName]);
            Assert.AreEqual(globalHeaderValue, (string)response["headers"][GlobalHeaderName]);
        }

        /// <summary>
        /// Test whether two headers with the same key (one in request and one in global) override each other
        /// with the request header taking priority
        /// </summary>
        [TestCase]
        public void LevelOverrideHeaderTest()
        {
            var request = new EchoRequest();

            var globalHeaderValue = Guid.NewGuid().ToString();
            var requestHeaderValue = Guid.NewGuid().ToString();

            Client.Headers[GlobalHeaderName] = globalHeaderValue;
            request.WithHeader(GlobalHeaderName, requestHeaderValue);

            var response = Client.Perform<JObject>(request);
            Assert.AreEqual(requestHeaderValue, (string)response["headers"]![GlobalHeaderName]);
        }
    }
}
