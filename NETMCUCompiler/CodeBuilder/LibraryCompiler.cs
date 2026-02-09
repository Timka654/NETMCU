using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;

namespace NETMCUCompiler.CodeBuilder
{
    public class LibraryCompiler
    {
        record TempDataRecord(SyntaxNode RootNode
            , SemanticModel SemanticModel
            , ClassDeclarationSyntax[] Classes
            , StructDeclarationSyntax[] Structs
            , EnumDeclarationSyntax[] Enums
            , FieldDeclarationSyntax[] Consts
            , MethodDeclarationSyntax[] Methods);

        public static string GetFullNodeName(SyntaxNode node)
        {
            var names = new Stack<string>();

            var current = node;

            while (current != null)
            {
                if (current is BaseTypeDeclarationSyntax classDecl) // class, struct, enum
                    names.Push(classDecl.Identifier.Text);
                else if (current is MethodDeclarationSyntax methodDecl) // method
                    names.Push(methodDecl.Identifier.Text);
                else if (current is EnumMemberDeclarationSyntax enumDecl) // enum
                    names.Push(enumDecl.Identifier.Text);
                else if (current is BaseNamespaceDeclarationSyntax nsDecl) // namespace
                    names.Push(nsDecl.Name.ToString());
                else if (current is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax varDecl) // var
                    names.Push(varDecl.Identifier.Text);
                else if (current is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax) { }
                else if (current is Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax) { }
                else if (current is CompilationUnitSyntax) { }
                else
                    throw new InvalidCastException($"Unsupported syntax node type: {current.GetType().FullName}");


                current = current.Parent;

                ////nameof(EnumDeclarationSyntax.Identifier)
                ////nameof(MethodDeclarationSyntax.Identifier)
                //nameof(FieldDeclarationSyntax.ToFullString)
            }

            return string.Join(".", names);
        }

        public static void CompileProject(Compilation compilation, CompilationContext context)
        {
            var items = compilation.SyntaxTrees.Select(tree =>
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);
                var rootDesc = root.DescendantNodes();

                var types = rootDesc
                .OfType<TypeDeclarationSyntax>()
                .ToArray();

                var classes = types.OfType<ClassDeclarationSyntax>()
                .Where(type => !context.ExceptTypes.Contains(type))
                .OrderByDescending(x => x == context.ProgramClass)
                .ToArray();

                var structs = types.OfType<StructDeclarationSyntax>()
                .Where(type => !context.ExceptTypes.Contains(type))
                .ToArray();

                var enums = rootDesc.OfType<EnumDeclarationSyntax>()
                .ToArray();

                var consts = rootDesc.OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
                .ToArray();

                var methods = rootDesc.OfType<MethodDeclarationSyntax>()
                .Where(method => !context.ExceptMethods.Contains(method))
                .Where(method=> method.Parent is not ClassDeclarationSyntax cls || !context.ExceptTypes.Contains(cls))
                .OrderByDescending(x => x == context.MainMethod)
                .ToArray();

                return new TempDataRecord(root, model, classes, structs, enums, consts, methods);
            }).ToArray();


            foreach (var item in items)
            {
                foreach (var @enum in item.Enums)
                {
                    var isPublic = @enum.Modifiers.Any(m => m.Text == "public");
                    foreach (var member in @enum.Members)
                    {
                        // Получаем значение через семантическую модель (Roslyn сам посчитает 0, 1, 2...)
                        var symbol = item.SemanticModel.GetDeclaredSymbol(member);

                        if (symbol?.ConstantValue != null)
                        {
                            var fullName = GetFullNodeName(member);
                            var val = symbol.ConstantValue;

                            context.RegisterConstant(fullName, val, isPublic);
                        }
                    }
                }

                foreach (var @const in item.Consts)
                {
                    foreach (var @var in @const.Declaration.Variables)
                    {
                        if (@var.Initializer?.Value is LiteralExpressionSyntax literal)
                        {
                            int val = ASMInstructions.ParseLiteral(literal);

                            context.RegisterConstant(GetFullNodeName(@var), val, @const.Modifiers.Any(m => m.Text == "public"));
                        }
                    }
                }

                foreach (var @class in item.Classes)
                {
                    context.RegisterType(GetFullNodeName(@class), @class);
                }

                foreach (var @class in item.Structs)
                {
                    context.RegisterType(GetFullNodeName(@class), @class);
                }
            }

            foreach (var item in items)
            {
                context.SemanticModel = item.SemanticModel;

                foreach (var method in item.Methods)
                {
                    CompileMethod(method, context);
                }
            }
        }

        private static void CompileMethod(MethodDeclarationSyntax method, CompilationContext ctx)
        {
            if (method.Body == null && method.ExpressionBody == null) return;

            ctx.RegisterMap.Clear();
            ctx.NextFreeRegister = 4;

            // СБОР ЛОКАЛЬНЫХ КОНСТАНТ МЕТОДА (вторая часть BuildAsm)
            var localConsts = method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                                .Where(s => s.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)));

            foreach (var localConst in localConsts)
            {
                foreach (var v in localConst.Declaration.Variables)
                {
                    if (v.Initializer?.Value is LiteralExpressionSyntax lit)
                        ctx.RegisterConstant(v.Identifier.Text, ASMInstructions.ParseLiteral(lit), false);
                }
            }


            var methodSymbol = ctx.SemanticModel.GetDeclaredSymbol(method) as IMethodSymbol;

            // Полное имя: Namespace.ClassName.MethodName
            string fullName = methodSymbol.ToDisplayString();

            bool isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            ctx.RegisterMethod(fullName, isStatic);

            // Настраиваем фрейм (с учетом 'this' если метод не static)
            ASMInstructions.EmitMethodPrologue(!isStatic, ctx);

            var declarations = method.DescendantNodes().OfType<VariableDeclarationSyntax>();
            foreach (var decl in declarations)
            {
                // 1. Пытаемся получить символ типа через семантическую модель
                var typeSymbol = ctx.SemanticModel.GetTypeInfo(decl.Type).Type;

                // Если по какой-то причине символ не определен, откатываемся к ToString()
                // Но для 'var' здесь уже будет реальное имя (например, GPIO_InitTypeDef)
                string typeName = typeSymbol?.ToDisplayString() ?? decl.Type.ToString();

                foreach (var v in decl.Variables)
                {
                    // Теперь ctx получит правильное имя типа даже для var
                    ctx.AllocateOnStack(v.Identifier.Text, typeName);
                }
            }
            // Пользуемся нашим старым добрым билдером для внутренностей
            var builder = new Stm32MethodBuilder(ctx);
            builder.Visit(method.Body);

            ASMInstructions.EmitMethodEpilogue(ctx);
        }
    }
}
