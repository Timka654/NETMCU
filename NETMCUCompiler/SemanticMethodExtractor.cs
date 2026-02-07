using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NETMCUCompiler
{
    public class SemanticMethodExtractor
    {
        public IEnumerable<configureRecord> Analyze(Compilation compilation, SemanticModel semanticModel)
        {
            // Проходим по всем деревьям синтаксиса в проекте
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();

                // Ищем все классы
                var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classDecl in classes)
                {
                    var classSymbol = semanticModel.GetDeclaredSymbol(classDecl);

                    // 1. Проверка наследования от нужного класса
                    if (IsDerivedFrom(classSymbol, "System.MCU.Compiler.ConfigureEntry"))
                    {
                        return ExtractInvocations(classDecl, semanticModel);
                    }
                }
            }

            return null;
        }

        public IEnumerable<configureRecord> ExtractInvocations(ClassDeclarationSyntax classDecl, SemanticModel model)
        {
            // Ищем метод Apply
            var applyMethod = classDecl.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Apply" &&
                                    m.Modifiers.Any(SyntaxKind.OverrideKeyword));

            if (applyMethod != null)
            {
                return ExtractInvocations(applyMethod, model);
            }

            return null;
        }
        public IEnumerable<configureRecord> ExtractInvocations(MethodDeclarationSyntax method, SemanticModel model)
        {
            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var t = model.GetSymbolInfo(invocation);
                // Получаем символ вызываемого метода (Symbol)
                var methodSymbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

                if (methodSymbol != null)
                {
                    //Console.WriteLine($"Method: {methodSymbol.Name}");
                    //Console.WriteLine($"Full Qualified Name: {methodSymbol.ToDisplayString()}");
                    //List<object> _args = new List<object>();
                    Dictionary<string, object> _args2 = new();
                    int i = 0;
                    // Работа с аргументами
                    foreach (var arg in invocation.ArgumentList.Arguments)
                    {
                        var nc = arg.NameColon?.Name.ToString();

                        _args2.Add(nc ?? methodSymbol.Parameters[i].Name, arg.Expression);

                        ++i;
                        //Console.WriteLine($" - Argument: {arg.Expression}, Type: {argType?.ToDisplayString()}");
                    }
                    yield return new configureRecord(methodSymbol.Name, methodSymbol, _args2);

                }
            }
        }

        private bool IsDerivedFrom(INamedTypeSymbol symbol, string fullyQualifiedBaseName)
        {
            var current = symbol?.BaseType;
            while (current != null)
            {
                if (current.ToDisplayString() == fullyQualifiedBaseName)
                    return true;
                current = current.BaseType;
            }
            return false;
        }
    }
}
