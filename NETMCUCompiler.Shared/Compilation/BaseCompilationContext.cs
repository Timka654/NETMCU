namespace NETMCUCompiler.Shared.Compilation
{
    public record RelocationRecord(MethodCompilationContext Context, int Offset, bool isStatic);

    public record StackVariable(string Name, TypeCompilationContext Metadata, int StackOffset);

    public abstract class BaseCompilationContext
    {
        public Dictionary<string, BaseCompilationContext> Childs { get; } = new();

        public BaseCompilationContext? ParentContext { get; set; }

        public CompilationContext? CompilationContext => ParentContext is CompilationContext global ? global : ParentContext?.CompilationContext;

        protected BaseCompilationContext(BaseCompilationContext? ParentContext)
        {
            this.ParentContext = ParentContext;
        }

        public abstract CompilationContextTypeEnum ContextType { get; }
    }
}
