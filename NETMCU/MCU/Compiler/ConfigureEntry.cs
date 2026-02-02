using System;

namespace System.MCU.Compiler
{
    public abstract class ConfigureEntry
    {
        public abstract void Apply(CompilerOptions options);
    }
}
