namespace NETMCUCompiler.CodeBuilder
{
    // В CompilationContext.cs добавь:
    public class StackVariable
    {
        public string Name { get; set; }
        public TypeCompilationContext Metadata { get; set; }
        public int StackOffset { get; set; } // Смещение от SP (указателя стека)

    }
}
