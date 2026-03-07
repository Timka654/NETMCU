using System;
using System.MCU.Compiler.Attributes;

namespace System.MCU.Compiler
{

    [CompilerType]
    public abstract class ConfigureEntry
    {
        public abstract void Apply(CompilerOptions options);
    }
}
