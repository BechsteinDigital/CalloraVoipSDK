using System.Reflection;
using System.Text;
using Xunit;

namespace CalloraVoipSdk.ArchitectureTests;

/// <summary>
/// ADR-006 §4 API-Surface-Gate. Erfasst reflexiv die exportierte (public) API der
/// consumer-facing Assemblies und vergleicht sie gegen eine eingecheckte Baseline
/// (<c>PublicApi.approved.txt</c>). Jede additive, entfernte oder geaenderte oeffentliche
/// Signatur faellt im Diff auf und verlangt einen bewussten, reviewten Baseline-Update im
/// selben PR — genau die vor Merge sichtbare Erkennung, die ADR-006 §4 fordert.
///
/// Erfasste Assemblies (die zwei, die ein externer Consumer referenziert):
///   • CalloraVoipSdk.Client — oeffentliche Facade: VoipClient/IVoipClient, WebRtc, Hosting,
///     DependencyInjection, Modules.
///   • CalloraVoipSdk.Core  — oeffentliche Domain-/Application-/Config-Typen, die durch die
///     Facade erreichbar sind (ICall, CallState, DialOptions, SdkConfiguration, …).
/// Nicht erfasst: interne Typen (das Testprojekt steht bewusst NICHT in Cores
/// InternalsVisibleTo, sieht also exakt die Consumer-Sicht), sowie optionale Plugin-Assemblies
/// wie CalloraVoipSdk.Audio.* — deren Surface ist separat und gehoert nicht in dieses Kern-Gate.
///
/// Der Dump ist deterministisch (alles OrdinalIgnoreCase sortiert, keine
/// Reflection-Reihenfolge-Abhaengigkeit); Compiler-generierte Member (Accessoren,
/// record-Boilerplate wie &lt;Clone&gt;$/EqualityContract/PrintMembers, Backing-Felder,
/// Operatoren-Paare) werden herausgefiltert bzw. normalisiert, damit die Baseline nicht bruechig
/// wird.
///
/// Baseline bewusst aktualisieren:  UPDATE_PUBLIC_API=1 dotnet test ...ArchitectureTests
/// (schreibt PublicApi.approved.txt neu, statt zu asserten).
/// </summary>
public sealed class PublicApiSurfaceTests
{
    private const string BaselineFileName = "PublicApi.approved.txt";
    private const string UpdateEnvVar = "UPDATE_PUBLIC_API";

    /// <summary>
    /// Die Assembly-Namen der consumer-facing Assemblies. Aufgeloest ueber Referenztypen,
    /// damit ProjectReferences tatsaechlich geladen sind (kein AppDomain-Rate-Spiel).
    /// </summary>
    /// <remarks>
    /// Every assembly a consumer can install has to be anchored here, and the audio packages were not:
    /// their surface could change in any direction without the gate noticing, which is the one thing
    /// this test exists to prevent. They are shipped separately and referenced directly — an
    /// application that writes its own <c>IAudioDevice</c> binds their types, not the facade's.
    /// </remarks>
    private static readonly Type[] AssemblyAnchors =
    [
        typeof(CalloraVoipSdk.IVoipClient),                                  // CalloraVoipSdk.Client
        typeof(CalloraVoipSdk.Core.Domain.Calls.ICall),                      // CalloraVoipSdk.Core
        typeof(CalloraVoipSdk.Audio.Abstractions.Processing.ActiveCodec),    // .Audio.Abstractions
        typeof(CalloraVoipSdk.Audio.Windows.WindowsAudioDevice),              // .Audio.Windows
        typeof(CalloraVoipSdk.Audio.Linux.LinuxAudioDevice),                  // .Audio.Linux
    ];

    [Fact]
    public void Public_API_Surface_matches_approved_baseline()
    {
        var dump = BuildSurfaceDump();
        var baselinePath = BaselinePath();

        if (ShouldUpdateBaseline())
        {
            File.WriteAllText(baselinePath, dump);
            return;
        }

        if (!File.Exists(baselinePath))
        {
            Assert.Fail(
                $"Baseline '{BaselineFileName}' fehlt. Einmalig erzeugen mit:\n" +
                $"  {UpdateEnvVar}=1 dotnet test tests/CalloraVoipSdk.ArchitectureTests/CalloraVoipSdk.ArchitectureTests.csproj");
        }

        var approved = File.ReadAllText(baselinePath);
        AssertSurfaceMatches(approved, dump);
    }

    private static bool ShouldUpdateBaseline()
    {
        var value = Environment.GetEnvironmentVariable(UpdateEnvVar);
        return !string.IsNullOrEmpty(value) &&
               (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static string BaselinePath()
        => Path.Combine(SourceScan.RepoRoot, "tests", "CalloraVoipSdk.ArchitectureTests", BaselineFileName);

    private static void AssertSurfaceMatches(string approved, string current)
    {
        var approvedLines = SplitLines(approved).ToHashSet(StringComparer.Ordinal);
        var currentLines = SplitLines(current).ToHashSet(StringComparer.Ordinal);

        var added = currentLines.Except(approvedLines).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = approvedLines.Except(currentLines).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        var message = new StringBuilder();
        message.AppendLine(
            "Oeffentliche API-Surface weicht von der Baseline ab (ADR-006 §4). " +
            "Ist die Aenderung beabsichtigt und reviewt, Baseline im selben PR neu erzeugen:");
        message.AppendLine(
            $"  {UpdateEnvVar}=1 dotnet test tests/CalloraVoipSdk.ArchitectureTests/CalloraVoipSdk.ArchitectureTests.csproj");

        if (added.Count > 0)
        {
            message.AppendLine($"\n  HINZUGEKOMMEN ({added.Count}) — additive API, bewusst baselinen:");
            foreach (var line in added)
            {
                message.AppendLine($"    + {line}");
            }
        }

        if (removed.Count > 0)
        {
            message.AppendLine($"\n  ENTFERNT/GEAENDERT ({removed.Count}) — potenziell BREAKING (ADR-006 §2):");
            foreach (var line in removed)
            {
                message.AppendLine($"    - {line}");
            }
        }

        Assert.Fail(message.ToString());
    }

    private static IEnumerable<string> SplitLines(string content)
        => content.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0);

    // ---- Surface-Erfassung -------------------------------------------------

    private static string BuildSurfaceDump()
    {
        var lines = new List<string>();

        foreach (var anchor in AssemblyAnchors)
        {
            var assembly = anchor.Assembly;
            foreach (var type in assembly.GetExportedTypes())
            {
                // Verschachtelte exportierte Typen werden separat von GetExportedTypes() geliefert;
                // hier ueberspringen wir sie nicht — jeder exportierte Typ bekommt seine eigene Zeile.
                lines.Add(DescribeType(type));
                lines.AddRange(DescribeMembers(type));
            }
        }

        var ordered = lines
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(
            "# CalloraVoipSdk public API surface baseline (ADR-006 §4).\n" +
            "# Auto-generated by PublicApiSurfaceTests. Update via: UPDATE_PUBLIC_API=1 dotnet test ...\n" +
            "# Assemblies: CalloraVoipSdk.Client, CalloraVoipSdk.Core. Sorted OrdinalIgnoreCase.\n");
        foreach (var line in ordered)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string DescribeType(Type type)
    {
        var kind = TypeKind(type);
        var name = TypeName(type);
        return $"TYPE {kind} {name}";
    }

    private static string TypeKind(Type type)
    {
        if (type.IsEnum)
        {
            return "enum";
        }

        if (type.IsInterface)
        {
            return "interface";
        }

        if (type.IsValueType)
        {
            return IsRecord(type) ? "record struct" : "struct";
        }

        // Delegate?
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return "delegate";
        }

        return IsRecord(type) ? "record" : "class";
    }

    private static bool IsRecord(Type type)
        // Records tragen einen compiler-generierten EqualityContract bzw. eine PrintMembers-Methode.
        => type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
               binder: null, types: [typeof(StringBuilder)], modifiers: null) is not null
           || type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null;

    private static IEnumerable<string> DescribeMembers(Type type)
    {
        var results = new List<string>();
        var typeName = TypeName(type);

        if (type.IsEnum)
        {
            foreach (var value in Enum.GetNames(type).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                results.Add($"  {typeName}.{value} = enum-value");
            }

            return results;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var member in type.GetMembers(flags))
        {
            if (!IsConsumerVisible(member))
            {
                continue;
            }

            var line = DescribeMember(typeName, member);
            if (line is not null)
            {
                results.Add(line);
            }
        }

        return results;
    }

    private static bool IsConsumerVisible(MemberInfo member)
    {
        // Compiler-generierte / rausgefilterte Namen (record-Boilerplate, backing fields, accessors).
        var name = member.Name;
        if (name.Contains('<') || name.Contains('>'))
        {
            return false;
        }

        if (name == "EqualityContract" || name == "PrintMembers")
        {
            return false;
        }

        return member switch
        {
            MethodInfo m => IsVisibleMethod(m),
            PropertyInfo p => IsVisibleProperty(p),
            EventInfo e => IsVisibleEvent(e),
            FieldInfo f => IsVisibleField(f),
            ConstructorInfo c => c.IsPublic || c.IsFamily,
            _ => false,
        };
    }

    private static bool IsVisibleMethod(MethodInfo m)
    {
        if (!m.IsPublic && !m.IsFamily)
        {
            return false;
        }

        // Property-/Event-Accessoren separat via Property/Event beschrieben.
        if (m.IsSpecialName)
        {
            return false;
        }

        return true;
    }

    private static bool IsVisibleProperty(PropertyInfo p)
    {
        var getter = p.GetMethod;
        var setter = p.SetMethod;
        var getVisible = getter is not null && (getter.IsPublic || getter.IsFamily);
        var setVisible = setter is not null && (setter.IsPublic || setter.IsFamily);
        return getVisible || setVisible;
    }

    private static bool IsVisibleEvent(EventInfo e)
    {
        var add = e.AddMethod;
        return add is not null && (add.IsPublic || add.IsFamily);
    }

    private static bool IsVisibleField(FieldInfo f)
        => f.IsPublic || f.IsFamily;

    private static string? DescribeMember(string typeName, MemberInfo member)
        => member switch
        {
            ConstructorInfo c => $"  {typeName}..ctor({FormatParameters(c.GetParameters())}){Access(c)}",
            MethodInfo m => $"  {typeName}.{m.Name}({FormatParameters(m.GetParameters())}) : {TypeName(m.ReturnType)}{Access(m)}",
            PropertyInfo p => DescribeProperty(typeName, p),
            EventInfo e => $"  {typeName}.{e.Name} : event {TypeName(e.EventHandlerType!)}",
            FieldInfo f => $"  {typeName}.{f.Name} : {TypeName(f.FieldType)}{(f.IsInitOnly ? " readonly" : string.Empty)}{(f.IsLiteral ? " const" : string.Empty)}{FieldAccess(f)}",
            _ => null,
        };

    private static string DescribeProperty(string typeName, PropertyInfo p)
    {
        var getter = p.GetMethod;
        var setter = p.SetMethod;
        var accessors = new List<string>();
        if (getter is not null && (getter.IsPublic || getter.IsFamily))
        {
            accessors.Add(getter.IsFamily ? "protected get" : "get");
        }

        if (setter is not null && (setter.IsPublic || setter.IsFamily))
        {
            // init-only Setter tragen den RequiresLocation/IsExternalInit-Modreq.
            var isInit = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
            var keyword = isInit ? "init" : "set";
            accessors.Add(setter.IsFamily ? $"protected {keyword}" : keyword);
        }

        var index = p.GetIndexParameters();
        var indexer = index.Length > 0 ? $"[{FormatParameters(index)}]" : string.Empty;
        return $"  {typeName}.{p.Name}{indexer} : {TypeName(p.PropertyType)} {{ {string.Join(", ", accessors)} }}";
    }

    private static string Access(MethodBase m) => m.IsFamily ? " [protected]" : string.Empty;

    private static string FieldAccess(FieldInfo f) => f.IsFamily ? " [protected]" : string.Empty;

    private static string FormatParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(FormatParameter));

    private static string FormatParameter(ParameterInfo p)
    {
        var type = TypeName(p.ParameterType);
        var modifier = string.Empty;
        if (p.ParameterType.IsByRef)
        {
            modifier = p.IsOut ? "out " : (p.IsIn ? "in " : "ref ");
        }

        var name = p.Name ?? "?";
        var optional = p.IsOptional ? " = default" : string.Empty;
        return $"{modifier}{type} {name}{optional}";
    }

    // ---- Namensnormalisierung ---------------------------------------------

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
        {
            return TypeName(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var name = TrimArity(FullName(def));
            var args = type.GetGenericArguments().Select(TypeName);
            return $"{name}<{string.Join(", ", args)}>";
        }

        return FullName(type);
    }

    private static string FullName(Type type)
    {
        // Nested Typen: Deklarations-Kette mit '+', wie von Reflection geliefert, aber lesbar mit '.'.
        var full = type.FullName ?? type.Name;
        return full.Replace('+', '.');
    }

    private static string TrimArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }
}
