﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.R.Core.AST.Definitions;

namespace Microsoft.R.Core.AST.DataTypes {
    /// <summary>
    /// Represents R function.
    /// </summary>
    [DebuggerDisplay("[{RFunction}]")]
    public class RFunction : RScalar<IRValueNode> {
        #region IRVector
        public override RMode Mode {
            get { return RMode.Function; }
        }
        #endregion

        public RFunction(IRValueNode value) :
            base(value) {
        }
    }
}
