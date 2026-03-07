namespace NETMCUCompiler.CodeBuilder
{
    public abstract class BaseCompilationContext
    {
        public Dictionary<string, BaseCompilationContext> Childs { get; } = new();

        public BaseCompilationContext? ParentContext { get; set; }

        protected BaseCompilationContext(BaseCompilationContext? ParentContext)
        {
            this.ParentContext = ParentContext;
        }

        public abstract CompilationContextTypeEnum ContextType { get; }
    }
}
