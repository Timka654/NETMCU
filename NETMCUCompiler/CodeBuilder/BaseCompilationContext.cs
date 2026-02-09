namespace NETMCUCompiler.CodeBuilder
{
    public abstract class BaseCompilationContext
    {
        public Dictionary<string, BaseCompilationContext> Childs { get; } = new();

        public required BaseCompilationContext? ParentContext { get; set; }

        public abstract CompilationContextTypeEnum ContextType { get; }
    }
}
