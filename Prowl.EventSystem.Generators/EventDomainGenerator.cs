// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Prowl.EventSystem.Generators;

[Generator]
public class EventDomainGenerator : IIncrementalGenerator
{
    private const string EventDomainAttrFqn = "Prowl.Runtime.EventSystem.EventDomainAttribute";
    private const string EventArgsAttrFqn = "Prowl.Runtime.EventSystem.EventArgsAttribute";
    private const string EventKeyFqn = "global::Prowl.Runtime.EventSystem.EventKey";
    private const string UnitFqn = "global::Prowl.Runtime.EventSystem.Unit";

    private static readonly DiagnosticDescriptor s_notPartialDiag = new(
        id: "PEVT0001",
        title: "EventDomain class must be partial",
        messageFormat: "The class '{0}' is marked with [EventDomain] but is not declared as 'partial'. Add the 'partial' modifier.",
        category: "Prowl.EventSystem",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                EventDomainAttrFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetDomainInfo(ctx, ct))
            .Where(static m => m is not null);

        context.RegisterSourceOutput(classDeclarations,
            static (spc, domain) => Execute(spc, domain!.Value));
    }

    #region Data model (value-equatable for incremental caching)

    private struct EventKeyInfo : IEquatable<EventKeyInfo>
    {
        public string Name;
        public string ArgsTypeFqn;
        public bool IsUnit;

        public bool Equals(EventKeyInfo other)
            => Name == other.Name
            && ArgsTypeFqn == other.ArgsTypeFqn
            && IsUnit == other.IsUnit;

        public override bool Equals(object? obj) => obj is EventKeyInfo o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (Name?.GetHashCode() ?? 0);
                h = h * 31 + (ArgsTypeFqn?.GetHashCode() ?? 0);
                h = h * 31 + IsUnit.GetHashCode();
                return h;
            }
        }
    }

    private struct ContainingTypeInfo : IEquatable<ContainingTypeInfo>
    {
        public string Keyword;       // "class", "struct", "record class", etc.
        public string Name;
        public string Accessibility;  // "public", "internal", etc.
        public bool IsStatic;

        public bool Equals(ContainingTypeInfo other)
            => Keyword == other.Keyword
            && Name == other.Name
            && Accessibility == other.Accessibility
            && IsStatic == other.IsStatic;

        public override bool Equals(object? obj) => obj is ContainingTypeInfo o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (Keyword?.GetHashCode() ?? 0);
                h = h * 31 + (Name?.GetHashCode() ?? 0);
                h = h * 31 + (Accessibility?.GetHashCode() ?? 0);
                h = h * 31 + IsStatic.GetHashCode();
                return h;
            }
        }
    }

    private struct EventDomainInfo : IEquatable<EventDomainInfo>
    {
        public string? Namespace;
        public string ClassName;
        public string ClassAccessibility;
        public bool IsStatic;
        public bool IsGlobal;
        public bool IsPartial;
        public EquatableArray<ContainingTypeInfo> ContainingTypes;
        public EquatableArray<EventKeyInfo> Events;
        public Location? DiagnosticLocation;

        public bool Equals(EventDomainInfo other)
            => Namespace == other.Namespace
            && ClassName == other.ClassName
            && ClassAccessibility == other.ClassAccessibility
            && IsStatic == other.IsStatic
            && IsGlobal == other.IsGlobal
            && IsPartial == other.IsPartial
            && ContainingTypes.Equals(other.ContainingTypes)
            && Events.Equals(other.Events);

        public override bool Equals(object? obj) => obj is EventDomainInfo o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (Namespace?.GetHashCode() ?? 0);
                h = h * 31 + (ClassName?.GetHashCode() ?? 0);
                h = h * 31 + (ClassAccessibility?.GetHashCode() ?? 0);
                h = h * 31 + IsStatic.GetHashCode();
                h = h * 31 + IsGlobal.GetHashCode();
                h = h * 31 + IsPartial.GetHashCode();
                h = h * 31 + ContainingTypes.GetHashCode();
                h = h * 31 + Events.GetHashCode();
                return h;
            }
        }
    }

    #endregion

    private static EventDomainInfo? GetDomainInfo(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.TargetNode;
        var classSymbol = (INamedTypeSymbol)ctx.TargetSymbol;

        bool isPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
        bool isStatic = classSymbol.IsStatic;

        // Extract Global property from [EventDomain] attribute
        bool isGlobal = false;
        foreach (var attrData in classSymbol.GetAttributes())
        {
            if (attrData.AttributeClass?.ToDisplayString() == "Prowl.Runtime.EventSystem.EventDomainAttribute")
            {
                foreach (var namedArg in attrData.NamedArguments)
                {
                    if (namedArg.Key == "Global" && namedArg.Value.Value is bool g)
                        isGlobal = g;
                }
                break;
            }
        }

        // Collect EventKey fields with optional [EventArgs]
        var events = new List<EventKeyInfo>();
        foreach (var member in classSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (member is IFieldSymbol field
                && field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == EventKeyFqn)
            {
                string argsType = UnitFqn; // default to Unit if no [EventArgs]
                foreach (var attr in field.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() == "Prowl.Runtime.EventSystem.EventArgsAttribute"
                        && attr.ConstructorArguments.Length == 1
                        && attr.ConstructorArguments[0].Value is ITypeSymbol typeArg
                        && typeArg.TypeKind != TypeKind.Error)
                    {
                        argsType = typeArg.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        break;
                    }
                }

                bool isUnit = argsType == UnitFqn;
                string eventName = field.Name.StartsWith("_") ? field.Name.Substring(1) : field.Name;
                events.Add(new EventKeyInfo
                {
                    Name = eventName,
                    ArgsTypeFqn = argsType,
                    IsUnit = isUnit,
                });
            }
        }

        if (events.Count == 0 && isPartial)
            return null; // nothing to generate

        // Collect containing type chain for nested classes
        var containingTypes = new List<ContainingTypeInfo>();
        var parent = classSymbol.ContainingType;
        while (parent is not null)
        {
            containingTypes.Insert(0, new ContainingTypeInfo
            {
                Keyword = parent.IsRecord ? "record class" : "class",
                Name = parent.Name,
                Accessibility = AccessibilityToString(parent.DeclaredAccessibility),
                IsStatic = parent.IsStatic,
            });
            parent = parent.ContainingType;
        }

        string? ns = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : classSymbol.ContainingNamespace.ToDisplayString();

        return new EventDomainInfo
        {
            Namespace = ns,
            ClassName = classSymbol.Name,
            ClassAccessibility = AccessibilityToString(classSymbol.DeclaredAccessibility),
            IsStatic = isStatic,
            IsGlobal = isGlobal,
            IsPartial = isPartial,
            ContainingTypes = new EquatableArray<ContainingTypeInfo>(containingTypes.ToArray()),
            Events = new EquatableArray<EventKeyInfo>(events.ToArray()),
            DiagnosticLocation = classDecl.Identifier.GetLocation(),
        };
    }

    private static void Execute(SourceProductionContext spc, EventDomainInfo domain)
    {
        // Emit diagnostic if class is not partial
        if (!domain.IsPartial)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                s_notPartialDiag,
                domain.DiagnosticLocation,
                domain.ClassName));
            return;
        }

        if (domain.Events.Length == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by Prowl.EventSystem.Generators — do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (domain.Namespace is not null)
        {
            sb.AppendLine($"namespace {domain.Namespace}");
            sb.AppendLine("{");
        }

        string indent = domain.Namespace is not null ? "    " : "";

        // Open containing types
        foreach (var ct in domain.ContainingTypes)
        {
            string staticMod = ct.IsStatic ? " static" : "";
            sb.AppendLine($"{indent}{ct.Accessibility}{staticMod} partial {ct.Keyword} {ct.Name}");
            sb.AppendLine($"{indent}{{");
            indent += "    ";
        }

        // Open the domain class
        string classMods = domain.IsStatic ? " static" : "";
        sb.AppendLine($"{indent}{domain.ClassAccessibility}{classMods} partial class {domain.ClassName}");
        sb.AppendLine($"{indent}{{");
        string ci = indent + "    "; // content indent

        // --- Generate enum ---
        sb.AppendLine($"{ci}/// <summary>Auto-generated enum backing the event keys in this domain.</summary>");
        sb.AppendLine($"{ci}public enum EventTypes");
        sb.AppendLine($"{ci}{{");
        foreach (var evt in domain.Events)
        {
            sb.AppendLine($"{ci}    [global::Prowl.Runtime.EventSystem.EventArgs(typeof({evt.ArgsTypeFqn}))]");
            sb.AppendLine($"{ci}    {evt.Name},");
        }
        sb.AppendLine($"{ci}}}");
        sb.AppendLine();

        // --- Generate manager ---
        string globalArg = domain.IsGlobal ? "global: true" : "";
        sb.AppendLine($"{ci}private static readonly global::Prowl.Runtime.EventSystem.EventManager<EventTypes> s_eventManager = new({globalArg});");
        sb.AppendLine();
        sb.AppendLine($"{ci}/// <summary>Gets the <see cref=\"global::Prowl.Runtime.EventSystem.EventManager{{T}}\"/> for this event domain.</summary>");
        sb.AppendLine($"{ci}public static global::Prowl.Runtime.EventSystem.EventManager<EventTypes> Manager => s_eventManager;");
        sb.AppendLine();

        // --- Generate event declarations for += / -= subscription syntax ---
        foreach (var evt in domain.Events)
        {
            string argsType = evt.ArgsTypeFqn;

            if (evt.IsUnit)
            {
                sb.AppendLine($"{ci}/// <summary>Subscribe to <see cref=\"EventTypes.{evt.Name}\"/> using += / -=. For priority control or IDisposable, use <see cref=\"Subscribe{evt.Name}\"/>.</summary>");
                sb.AppendLine($"{ci}public static event global::System.Action {evt.Name}");
                sb.AppendLine($"{ci}{{");
                sb.AppendLine($"{ci}    add => s_eventManager.AddNewDelegate(EventTypes.{evt.Name}, value, 0);");
                sb.AppendLine($"{ci}    remove => s_eventManager.RemoveDelegate(EventTypes.{evt.Name}, value);");
                sb.AppendLine($"{ci}}}");
            }
            else
            {
                sb.AppendLine($"{ci}/// <summary>Subscribe to <see cref=\"EventTypes.{evt.Name}\"/> using += / -=. For priority control or IDisposable, use <see cref=\"Subscribe{evt.Name}\"/>.</summary>");
                sb.AppendLine($"{ci}public static event global::System.Action<{argsType}> {evt.Name}");
                sb.AppendLine($"{ci}{{");
                sb.AppendLine($"{ci}    add => s_eventManager.AddNewDelegate<{argsType}>(EventTypes.{evt.Name}, value, 0);");
                sb.AppendLine($"{ci}    remove => s_eventManager.RemoveDelegate(EventTypes.{evt.Name}, value);");
                sb.AppendLine($"{ci}}}");
            }
        }
        sb.AppendLine();

        // --- Generate per-event convenience methods ---
        foreach (var evt in domain.Events)
        {
            string argsType = evt.ArgsTypeFqn;
            string containerType = $"global::Prowl.Runtime.EventSystem.EventDelegateContainer<EventTypes, {argsType}>";

            sb.AppendLine($"{ci}// --- {evt.Name} ---");
            sb.AppendLine();

            if (evt.IsUnit)
            {
                EmitUnitMethods(sb, ci, evt.Name, containerType);
            }
            else
            {
                EmitTypedMethods(sb, ci, evt.Name, argsType, containerType);
            }
        }

        // Close domain class
        sb.AppendLine($"{indent}}}");

        // Close containing types
        for (int i = domain.ContainingTypes.Length - 1; i >= 0; i--)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.AppendLine($"{indent}}}");
        }

        // Close namespace
        if (domain.Namespace is not null)
            sb.AppendLine("}");

        string hintName = domain.ContainingTypes.Length > 0
            ? string.Join(".", domain.ContainingTypes.Select(c => c.Name)) + "." + domain.ClassName + ".g.cs"
            : domain.ClassName + ".g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

    private static void EmitUnitMethods(StringBuilder sb, string ci, string name, string containerType)
    {
        // Invoke (parameterless)
        sb.AppendLine($"{ci}/// <summary>Invokes <see cref=\"EventTypes.{name}\"/> on this domain's manager.</summary>");
        sb.AppendLine($"{ci}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{ci}public static void Invoke{name}()");
        sb.AppendLine($"{ci}    => s_eventManager.InvokeEvent(EventTypes.{name});");
        sb.AppendLine();

        // GlobalInvoke (parameterless)
        sb.AppendLine($"{ci}/// <summary>Invokes <see cref=\"EventTypes.{name}\"/> across all global managers of this domain.</summary>");
        sb.AppendLine($"{ci}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{ci}public static void GlobalInvoke{name}()");
        sb.AppendLine($"{ci}    => global::Prowl.Runtime.EventSystem.EventManager<EventTypes>.GlobalInvokeEvent(EventTypes.{name});");
        sb.AppendLine();

        // Subscribe (parameterless)
        sb.AppendLine($"{ci}/// <summary>Subscribes a parameterless handler to <see cref=\"EventTypes.{name}\"/>. Dispose the returned container to unsubscribe.</summary>");
        sb.AppendLine($"#if DEBUG");
        sb.AppendLine($"{ci}public static {containerType} Subscribe{name}(");
        sb.AppendLine($"{ci}    global::System.Action handler, int priority = 0,");
        sb.AppendLine($"{ci}    [global::System.Runtime.CompilerServices.CallerFilePath] string? sourceFile = null,");
        sb.AppendLine($"{ci}    [global::System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,");
        sb.AppendLine($"{ci}    [global::System.Runtime.CompilerServices.CallerMemberName] string? sourceMember = null)");
        sb.AppendLine($"{ci}    => s_eventManager.AddNewDelegate(EventTypes.{name}, handler, priority, sourceFile, sourceLine, sourceMember);");
        sb.AppendLine($"#else");
        sb.AppendLine($"{ci}public static {containerType} Subscribe{name}(");
        sb.AppendLine($"{ci}    global::System.Action handler, int priority = 0)");
        sb.AppendLine($"{ci}    => s_eventManager.AddNewDelegate(EventTypes.{name}, handler, priority);");
        sb.AppendLine($"#endif");
        sb.AppendLine();
    }

    private static void EmitTypedMethods(StringBuilder sb, string ci, string name, string argsType, string containerType)
    {
        // Invoke (typed)
        sb.AppendLine($"{ci}/// <summary>Invokes <see cref=\"EventTypes.{name}\"/> on this domain's manager.</summary>");
        sb.AppendLine($"{ci}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{ci}public static void Invoke{name}({argsType} args)");
        sb.AppendLine($"{ci}    => s_eventManager.InvokeEvent(EventTypes.{name}, args);");
        sb.AppendLine();

        // GlobalInvoke (typed)
        sb.AppendLine($"{ci}/// <summary>Invokes <see cref=\"EventTypes.{name}\"/> across all global managers of this domain.</summary>");
        sb.AppendLine($"{ci}[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"{ci}public static void GlobalInvoke{name}({argsType} args)");
        sb.AppendLine($"{ci}    => global::Prowl.Runtime.EventSystem.EventManager<EventTypes>.GlobalInvokeEvent(EventTypes.{name}, args);");
        sb.AppendLine();

        // Subscribe (typed)
        sb.AppendLine($"{ci}/// <summary>Subscribes a typed handler to <see cref=\"EventTypes.{name}\"/>. Dispose the returned container to unsubscribe.</summary>");
        sb.AppendLine($"#if DEBUG");
        sb.AppendLine($"{ci}public static {containerType} Subscribe{name}(");
        sb.AppendLine($"{ci}    global::System.Action<{argsType}> handler, int priority = 0,");
        sb.AppendLine($"{ci}    [global::System.Runtime.CompilerServices.CallerFilePath] string? sourceFile = null,");
        sb.AppendLine($"{ci}    [global::System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,");
        sb.AppendLine($"{ci}    [global::System.Runtime.CompilerServices.CallerMemberName] string? sourceMember = null)");
        sb.AppendLine($"{ci}    => s_eventManager.AddNewDelegate<{argsType}>(EventTypes.{name}, handler, priority, sourceFile, sourceLine, sourceMember);");
        sb.AppendLine($"#else");
        sb.AppendLine($"{ci}public static {containerType} Subscribe{name}(");
        sb.AppendLine($"{ci}    global::System.Action<{argsType}> handler, int priority = 0)");
        sb.AppendLine($"{ci}    => s_eventManager.AddNewDelegate<{argsType}>(EventTypes.{name}, handler, priority);");
        sb.AppendLine($"#endif");
        sb.AppendLine();
    }

    private static string AccessibilityToString(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Private => "private",
        _ => "internal",
    };
}
