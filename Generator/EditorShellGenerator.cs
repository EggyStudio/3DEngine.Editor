using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Engine;

/// <summary>
/// Roslyn incremental generator that scans for <c>[Editor.Shell.EditorShell]</c> classes implementing
/// <c>IEditorShellBuilder</c> and <c>[Editor.Shell.EditorPanel]</c>-decorated Blazor components in the
/// consuming compilation, and emits a single <c>EditorShellsRegistration.g.cs</c> file with a static
/// method tagged <c>[GeneratedShellRegistration]</c>. The runtime <c>StaticShellLoader</c> reflects
/// across loaded assemblies for that attribute and invokes each method with a <c>ShellRegistry</c>.
/// </summary>
/// <remarks>
/// <para>
/// The generated method registers a single <c>ShellSource</c> (id =
/// <c>ShellSourceIds.Static</c>, <c>Precedence = 0</c>) containing every discovered
/// <c>IEditorShellBuilder</c> instance and every <c>[EditorPanel]</c> component type with its
/// attribute metadata. The runtime <c>RuntimeShellCompiler</c> registers a separate
/// <c>ShellSourceIds.Dynamic</c> source (Precedence = 100) so hot-reloaded shells override
/// statically-compiled ones on panel-id collisions.
/// </para>
/// <para>
/// Skipped (with diagnostic <c>EDS0001</c>): types that are abstract, generic, or lack a public
/// parameterless constructor.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EditorShellGenerator : IIncrementalGenerator
{
    private const string EditorShellAttr = "Editor.Shell.EditorShellAttribute";
    private const string EditorPanelAttr = "Editor.Shell.EditorPanelAttribute";
    private const string ShellBuilderIface = "Editor.Shell.IEditorShellBuilder";

    private static readonly DiagnosticDescriptor SkippedTypeDiag = new(
        id: "EDS0001",
        title: "EditorShell skipped",
        messageFormat: "Editor shell '{0}' skipped: {1}",
        category: "EditorShellGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var candidates = ctx.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
            static (context, _) =>
            {
                var cds = (ClassDeclarationSyntax)context.Node;
                return context.SemanticModel.GetDeclaredSymbol(cds) as INamedTypeSymbol;
            })
            .Where(s => s is not null)
            .Collect();

        var combined = ctx.CompilationProvider.Combine(candidates);

        ctx.RegisterSourceOutput(combined, (spc, pair) =>
        {
            var (compilation, types) = pair;

            var shellTypes = new List<INamedTypeSymbol>();
            var panelTypes = new List<(INamedTypeSymbol Type, AttributeData Attr)>();

            foreach (var t in types.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>())
            {
                AttributeData? shellAttr = null;
                AttributeData? panelAttr = null;
                foreach (var a in t.GetAttributes())
                {
                    var name = a.AttributeClass?.ToDisplayString();
                    if (name == EditorShellAttr) shellAttr = a;
                    else if (name == EditorPanelAttr) panelAttr = a;
                }

                if (shellAttr is not null)
                {
                    if (!ImplementsInterface(t, ShellBuilderIface))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(SkippedTypeDiag, t.Locations.FirstOrDefault(),
                            t.ToDisplayString(), $"does not implement {ShellBuilderIface}"));
                    }
                    else if (t.IsAbstract)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(SkippedTypeDiag, t.Locations.FirstOrDefault(),
                            t.ToDisplayString(), "type is abstract"));
                    }
                    else if (t.IsGenericType)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(SkippedTypeDiag, t.Locations.FirstOrDefault(),
                            t.ToDisplayString(), "type is generic"));
                    }
                    else if (!HasPublicParameterlessCtor(t))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(SkippedTypeDiag, t.Locations.FirstOrDefault(),
                            t.ToDisplayString(), "no public parameterless constructor"));
                    }
                    else
                    {
                        shellTypes.Add(t);
                    }
                }

                if (panelAttr is not null && !t.IsAbstract && !t.IsGenericType)
                {
                    panelTypes.Add((t, panelAttr));
                }
            }

            if (shellTypes.Count == 0 && panelTypes.Count == 0) return;

            var asmName = SanitizeIdentifier(compilation.AssemblyName ?? "Assembly");
            spc.AddSource("EditorShellsRegistration.g.cs", Emit(asmName, shellTypes, panelTypes));
        });
    }

    private static bool ImplementsInterface(INamedTypeSymbol t, string fqn)
    {
        foreach (var i in t.AllInterfaces)
            if (i.ToDisplayString() == fqn) return true;
        return false;
    }

    private static bool HasPublicParameterlessCtor(INamedTypeSymbol t)
    {
        // Implicit ctor counts.
        var explicitCtors = t.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).ToList();
        if (explicitCtors.Count == 0) return true;
        foreach (var c in explicitCtors)
            if (c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public) return true;
        return false;
    }

    private static string SanitizeIdentifier(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string Emit(
        string asmIdent,
        IReadOnlyList<INamedTypeSymbol> shellTypes,
        IReadOnlyList<(INamedTypeSymbol Type, AttributeData Attr)> panelTypes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace Editor.Shell.Generated;");
        sb.AppendLine();
        sb.AppendLine($"internal static class EditorShellsRegistration_{asmIdent}");
        sb.AppendLine("{");
        sb.AppendLine("    [global::Editor.Shell.GeneratedShellRegistration]");
        sb.AppendLine("    public static void Register(global::Editor.Shell.ShellRegistry registry)");
        sb.AppendLine("    {");

        sb.AppendLine("        var builders = new global::Editor.Shell.IEditorShellBuilder[]");
        sb.AppendLine("        {");
        foreach (var t in shellTypes)
            sb.AppendLine($"            new {t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}(),");
        sb.AppendLine("        };");
        sb.AppendLine();

        sb.AppendLine("        var panels = new global::System.Collections.Generic.List<(global::Editor.Shell.EditorPanelAttribute, global::System.Type)>");
        sb.AppendLine("        {");
        foreach (var (t, attr) in panelTypes)
        {
            var fqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"            ({EmitPanelAttributeCtor(attr)}, typeof({fqn})),");
        }
        sb.AppendLine("        };");
        sb.AppendLine();

        sb.AppendLine("        registry.RegisterSource(global::Editor.Shell.ShellSourceIds.Static, new global::Editor.Shell.ShellSource");
        sb.AppendLine("        {");
        sb.AppendLine("            Builders = builders,");
        sb.AppendLine("            PanelComponents = panels,");
        sb.AppendLine("            Precedence = 0,");
        sb.AppendLine("        });");

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Reproduces the call site of an <c>[EditorPanel(...)]</c> attribute as a runtime constructor expression.</summary>
    private static string EmitPanelAttributeCtor(AttributeData attr)
    {
        var sb = new StringBuilder();
        sb.Append("new global::Editor.Shell.EditorPanelAttribute(");
        for (int i = 0; i < attr.ConstructorArguments.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(EmitTypedConstant(attr.ConstructorArguments[i]));
        }
        sb.Append(')');
        if (attr.NamedArguments.Length > 0)
        {
            sb.Append(" { ");
            for (int i = 0; i < attr.NamedArguments.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var (k, v) = (attr.NamedArguments[i].Key, attr.NamedArguments[i].Value);
                sb.Append(k).Append(" = ").Append(EmitTypedConstant(v));
            }
            sb.Append(" }");
        }
        return sb.ToString();
    }

    private static string EmitTypedConstant(TypedConstant c)
    {
        if (c.IsNull) return "null";
        if (c.Kind == TypedConstantKind.Enum && c.Type is INamedTypeSymbol e)
            return $"({e.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){c.Value}";
        if (c.Value is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (c.Value is bool b) return b ? "true" : "false";
        if (c.Value is float f) return f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "f";
        if (c.Value is double d) return d.ToString(System.Globalization.CultureInfo.InvariantCulture) + "d";
        return c.Value?.ToString() ?? "null";
    }
}



