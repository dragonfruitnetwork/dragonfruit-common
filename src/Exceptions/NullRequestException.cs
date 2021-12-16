﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System;

namespace DragonFruit.Data.Exceptions
{
    public class NullRequestException : Exception
    {
        public NullRequestException()
            : base("The Request provided was null or has no path")
        {
        }
    }
}
