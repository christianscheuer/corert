﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Internal.Text;

namespace ILCompiler.DependencyAnalysis
{
    public class TypeManagerIndirectionNode : ObjectNode, ISymbolNode
    {
        public void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append(nameMangler.CompilationUnitPrefix).Append("__typemanager_indirection");
        }
        public int Offset => 0;
        public override bool IsShareable => false;

        protected override string GetName() => this.GetMangledName();

        public override ObjectNodeSection Section => ObjectNodeSection.DataSection;

        public override bool StaticDependenciesAreComputed => true;

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
        {
            ObjectDataBuilder objData = new ObjectDataBuilder(factory);
            objData.AddSymbol(this);
            objData.RequireInitialPointerAlignment();
            objData.EmitZeroPointer();
            objData.EmitZeroPointer();
            return objData.ToObjectData();
        }
    }
}
