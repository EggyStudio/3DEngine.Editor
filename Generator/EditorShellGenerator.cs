using System.Collections.Generic;
using System.Globalization;
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

        ctx.RegisterSourceOutput(ctx.CompilationProvider.Combine(candidates), (spc, pair) =>
        {
            var (compilation, types) = pair;

            var shellTypes = new List<INamedTypeSymbol>();
            var panelTypes = new List<(INamedTypeSymbol Type, AttributeData Attr)>();

            foreach (var t in types.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>())
            {
                var (shellAttr, panelAttr) = FindAttrs(t);

                if (shellAttr is not null)
                    TryRegisterShell(t, spc, shellTypes);

                if (panelAttr is not null && !t.IsAbstract && !t.IsGenericType)
                    panelTypes.Add((t, panelAttr));
            }

            if (shellTypes.Count == 0 && panelTypes.Count == 0) return;

            var asmName = SanitizeIdentifier(compilation.AssemblyName ?? "Assembly");
            spc.AddSource("EditorShellsRegistration.g.cs", Emit(asmName, shellTypes, panelTypes));
        });
    }

    // -- Discovery / validation --

    private static (AttributeData? Shell, AttributeData? Panel) FindAttrs(INamedTypeSymbol t)
    {
        AttributeData? shell = null, panel = null;
        foreach (var a in t.GetAttributes())
        {
            switch (a.AttributeClass?.ToDisplayString())
            {
                case EditorShellAttr: shell = a; break;
                case EditorPanelAttr: panel = a; break;
            }
        }

        return (shell, panel);
    }

    private static void TryRegisterShell(INamedTypeSymbol t, SourceProductionContext spc, List<INamedTypeSymbol> sink)
    {
        string? reason =
            !ImplementsInterface(t, ShellBuilderIface) ? $"does not implement {ShellBuilderIface}" :
            t.IsAbstract ? "type is abstract" :
            t.IsGenericType ? "type is generic" :
            !HasPublicParameterlessCtor(t) ? "no public parameterless constructor" :
            null;

        if (reason is null)
            sink.Add(t);
        else
            spc.ReportDiagnostic(Diagnostic.Create(SkippedTypeDiag, t.Locations.FirstOrDefault(),
                t.ToDisplayString(), reason));
    }

    private static bool ImplementsInterface(INamedTypeSymbol t, string fqn)
        => t.AllInterfaces.Any(i => i.ToDisplayString() == fqn);

    private static bool HasPublicParameterlessCtor(INamedTypeSymbol t)
    {
        // Implicit ctor counts.
        var explicitCtors = t.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).ToList();
        return explicitCtors.Count == 0
               || explicitCtors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
    }

    private static string SanitizeIdentifier(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    // -- Source emission --

    private static string Emit(
        string asmIdent,
        IReadOnlyList<INamedTypeSymbol> shellTypes,
        IReadOnlyList<(INamedTypeSymbol Type, AttributeData Attr)> panelTypes)
    {
        var builders = string.Concat(shellTypes.Select(t =>
            $"            new {t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}(),\n"));

        var panels = string.Concat(panelTypes.Select(p =>
        {
            var fqn = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"            ({EmitPanelAttributeCtor(p.Attr)}, typeof({fqn})),\n";
        }));

        return
            $$"""
              // <auto-generated />
              #nullable enable
              namespace Editor.Shell.Generated;

              internal static class EditorShellsRegistration_{{asmIdent}}
              {
                  [global::Editor.Shell.GeneratedShellRegistration]
                  public static void Register(global::Editor.Shell.ShellRegistry registry)
                  {
                      var builders = new global::Editor.Shell.IEditorShellBuilder[]
                      {
                          {{builders}}        
                      };

                      var panels = new global::System.Collections.Generic.List<(global::Editor.Shell.EditorPanelAttribute, global::System.Type)>
                      {
                          {{panels}}        
                      };

                      registry.RegisterSource(global::Editor.Shell.ShellSourceIds.Static, new global::Editor.Shell.ShellSource
                      {
                          Builders = builders,
                          PanelComponents = panels,
                          Precedence = 0,
                      });
                  }
              }

              """;
    }

    /// <summary>Reproduces the call site of an <c>[EditorPanel(...)]</c> attribute as a runtime constructor expression.</summary>
    private static string EmitPanelAttributeCtor(AttributeData attr)
    {
        var ctorArgs = string.Join(", ", attr.ConstructorArguments.Select(EmitTypedConstant));
        var named = attr.NamedArguments.Length == 0
            ? ""
            : " { " + string.Join(", ", attr.NamedArguments.Select(n => $"{n.Key} = {EmitTypedConstant(n.Value)}")) +
              " }";
        return $"new global::Editor.Shell.EditorPanelAttribute({ctorArgs}){named}";
    }

    private static string EmitTypedConstant(TypedConstant c)
    {
        if (c.IsNull) return "null";
        if (c.Kind == TypedConstantKind.Enum && c.Type is INamedTypeSymbol e)
            return $"({e.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){c.Value}";
        return c.Value switch
        {
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            bool b => b ? "true" : "false",
            float f => f.ToString(CultureInfo.InvariantCulture) + "f",
            double d => d.ToString(CultureInfo.InvariantCulture) + "d",
            null => "null",
            _ => c.Value.ToString() ?? "null",
        };
    }
}