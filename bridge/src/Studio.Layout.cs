using System.Reflection;
using ServiceStudioBridge.Layout;

namespace ServiceStudioBridge;

/// <summary>
/// layoutFlows: reads an open module's logic flows into the plain flowchart model (Layout/Flow.cs),
/// runs the layout (Layout/FlowLayout.cs), and writes the positions and note texts that changed
/// back to the module as one undoable command. Only the public Model API (OutSystems.Model.V1) and
/// Service Studio's public Command are used, all looked up by name.
/// </summary>
internal static partial class Studio
{
    /// <summary>The Model API members layoutFlows reads and writes, resolved once.</summary>
    private sealed class FlowMembers
    {
        public required Type ESpace, Action;
        public required MethodInfo Actions;                // IObjectSignature.GetAllDescendantsOfType<IAction>()
        public required PropertyInfo Nodes, X, Y, Text, Parent;
        public required (Type Type, NodeKind Kind)[] Kinds;
        public required PropertyInfo True, False, Cycle, Next, Conditions, Otherwise;
        public required PropertyInfo NodeConnectors, NoteConnectors, ConnectorTarget;
        public required MethodInfo Execute;                // Command.Execute(PresenterContext, string, Action)
    }

    private static FlowMembers? flowMembers;

    private static FlowMembers LayoutMembers()
    {
        if (flowMembers != null)
        {
            return flowMembers;
        }

        var espace = Reflect.FindType("OutSystems.Model.IESpace", "OutSystems.Model.V1");
        Type ModelType(string name) => espace.Assembly.GetType(name) ?? throw new BridgeException($"{name} is missing");
        // An interface declares its own properties only: look through the ones it extends too.
        PropertyInfo Property(Type type, string name) =>
            new[] { type }.Concat(type.GetInterfaces()).Select(t => t.GetProperty(name)).FirstOrDefault(p => p != null)
            ?? throw new BridgeException($"{type.Name}.{name} is missing");

        var signature = ModelType("OutSystems.Model.IObjectSignature");
        var action = ModelType("OutSystems.Model.Logic.IAction");
        var node = ModelType("OutSystems.Model.Logic.Nodes.IActionNode");
        var flowNode = ModelType("OutSystems.Model.IFlowNode");
        var comment = ModelType("OutSystems.Model.Logic.Nodes.ICommentNode");
        var ifNode = ModelType("OutSystems.Model.Logic.Nodes.IIfNode");
        var forEach = ModelType("OutSystems.Model.Logic.Nodes.IForEachNode");
        var switchNode = ModelType("OutSystems.Model.Logic.Nodes.ISwitchNode");
        var command = Reflect.FindType("ServiceStudio.Commands.Command", "ServiceStudio.Framework");

        return flowMembers = new FlowMembers
        {
            ESpace = espace,
            Action = action,
            Actions = (signature.GetMethod("GetAllDescendantsOfType") ?? throw new BridgeException("IObjectSignature.GetAllDescendantsOfType is missing"))
                .MakeGenericMethod(action),
            Nodes = Property(action, "Nodes"),
            X = Property(flowNode, "HorizontalPosition"),
            Y = Property(flowNode, "VerticalPosition"),
            Text = Property(comment, "Text"),
            Parent = Property(signature, "Parent"),
            Kinds =
            [
                (ModelType("OutSystems.Model.Logic.Nodes.IStartNode"), NodeKind.Start),
                (ModelType("OutSystems.Model.Logic.Nodes.IEndNode"), NodeKind.End),
                (ModelType("OutSystems.Model.Logic.Nodes.IRaiseExceptionNode"), NodeKind.Raise),
                (ModelType("OutSystems.Model.Logic.Nodes.IExceptionHandlerNode"), NodeKind.ExceptionHandler),
                (ifNode, NodeKind.If),
                (forEach, NodeKind.ForEach),
                (switchNode, NodeKind.Switch),
                (comment, NodeKind.Comment),
            ],
            True = Property(ifNode, "TrueTarget"),
            False = Property(ifNode, "FalseTarget"),
            Cycle = Property(forEach, "CycleTarget"),
            Next = Property(forEach, "Target"),
            Conditions = Property(switchNode, "Conditions"),
            Otherwise = Property(switchNode, "OtherwiseTarget"),
            NodeConnectors = Property(node, "Connectors"),
            // ICommentNode re-declares Connectors as its links to nodes.
            NoteConnectors = Property(comment, "Connectors"),
            ConnectorTarget = Property(ModelType("OutSystems.Model.Logic.IActionConnector"), "Target"),
            Execute = command.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "Execute" && m.GetParameters() is { Length: 3 } p && p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(System.Action)),
        };
    }

    /// <summary>
    /// Lays out the logic flows of a module open in Service Studio. flows names some, by path or
    /// name, comma-separated; empty lays out all. A dry run changes nothing and says what would move.
    /// </summary>
    public static object LayoutFlows(string? host, string module, string? flows, bool dryRun)
    {
        var (connection, tab, name) = ResolveOpenModule(host, module);
        if (tab == null)
        {
            throw NotOpen(module, connection);
        }

        module = name;
        var eSpace = Reflect.Get(tab, "OwnerESpace") ?? throw new BridgeException($"Service Studio's tab for {module} has no module loaded yet.");

        // Positions changed in the live module while Service Studio's MCP server has a working copy
        // checked out would be put back by its next omlMerge, which keeps the working copy's side.
        string? warning = null;
        switch (EditSessionOn(eSpace, out var sessionProblem))
        {
            case true when !dryRun:
                throw new BridgeException($"Service Studio's MCP server has an edit session (or unmerged changes) on {module}, and its next omlMerge "
                    + "would put the old positions back. Lay the flows out once the session has ended: after its last merge and endOmlEditSession.");
            case null:
                warning = $"The bridge couldn't check for an edit session on {module} ({sessionProblem}). If one is open, its next omlMerge puts the old positions back.";
                break;
        }

        var m = LayoutMembers();
        var only = (flows ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Read in Service Studio's context; lay out outside it, so the window stays responsive.
        var model = new List<Flow>();
        var read = new List<(object Node, FlowNode Copy, int X, int Y, string? Text)>();
        InContext(tab, $"Reading the flows of {module}", () => ReadFlows(m, eSpace, model, read), wait: true);
        var report = FlowLayout.Arrange(model, only);

        var changed = read.Where(r => r.Copy.X != r.X || r.Copy.Y != r.Y || r.Copy.Kind == NodeKind.Comment && r.Copy.Text != r.Text).ToList();
        if (!dryRun && changed.Count > 0)
        {
            InContext(tab, $"Laying out the flows of {module}", () =>
            {
                System.Action write = () =>
                {
                    foreach (var (node, copy, x, y, text) in changed)
                    {
                        if (copy.X != x || copy.Y != y)
                        {
                            m.X.SetValue(node, copy.X);
                            m.Y.SetValue(node, copy.Y);
                        }

                        if (copy.Kind == NodeKind.Comment && copy.Text != text)
                        {
                            m.Text.SetValue(node, copy.Text);
                        }
                    }
                };
                m.Execute.Invoke(null, [Reflect.Call(tab, "GetPresenterContext"), "Layout Flows", write]);
            }, wait: true);
        }

        return Scoped(connection, new
        {
            Module = module,
            Status = dryRun ? "dryRun" : changed.Count == 0 ? "unchanged" : "laidOut",
            FlowsScanned = report.Scanned,
            FlowsChanged = report.Changing,
            NodesMoved = report.Moving,
            NotesWrapped = report.Wrapping,
            Flows = report.Lines.Take(200).ToList(),
            Note = !dryRun && changed.Count > 0 ? "Unsaved in Service Studio, as one undoable step (Layout Flows)." : null,
            Warning = warning,
        });
    }

    /// <summary>Every action's nodes into the model, with each real node and its values as read.</summary>
    private static void ReadFlows(FlowMembers m, object eSpace, List<Flow> model, List<(object, FlowNode, int, int, string?)> read)
    {
        foreach (var action in Reflect.Enumerate(m.Actions.Invoke(eSpace, null)))
        {
            var flow = new Flow { Path = PathOf(m, action) };
            var nodes = Reflect.Enumerate(m.Nodes.GetValue(action));
            var copies = new Dictionary<object, FlowNode>(ReferenceEqualityComparer.Instance);
            foreach (var node in nodes)
            {
                var kind = m.Kinds.FirstOrDefault(k => k.Type.IsInstanceOfType(node)).Kind;
                var copy = new FlowNode
                {
                    Kind = kind,
                    X = (int)m.X.GetValue(node)!,
                    Y = (int)m.Y.GetValue(node)!,
                    Text = kind == NodeKind.Comment ? m.Text.GetValue(node) as string : null,
                };
                copies[node] = copy;
                flow.Nodes.Add(copy);
                read.Add((node, copy, copy.X, copy.Y, copy.Text));
            }

            foreach (var node in nodes)
            {
                var copy = copies[node];
                copy.Targets.AddRange(TargetsOf(m, node, copy.Kind).Select(t => t != null && copies.TryGetValue(t, out var target) ? target : null));
            }

            model.Add(flow);
        }
    }

    /// <summary>A node's targets in branch order, as FlowNode.Targets describes it.</summary>
    private static IEnumerable<object?> TargetsOf(FlowMembers m, object node, NodeKind kind) => kind switch
    {
        NodeKind.If => [m.True.GetValue(node), m.False.GetValue(node)],
        NodeKind.ForEach => [m.Cycle.GetValue(node), m.Next.GetValue(node)],
        NodeKind.Switch => Reflect.Enumerate(m.Conditions.GetValue(node)).Select(c => m.ConnectorTarget.GetValue(c)).Append(m.Otherwise.GetValue(node)),
        NodeKind.Comment => Reflect.Enumerate(m.NoteConnectors.GetValue(node)).Select(c => m.ConnectorTarget.GetValue(c)),
        _ => Reflect.Enumerate(m.NodeConnectors.GetValue(node)).Select(c => m.ConnectorTarget.GetValue(c)),
    };

    /// <summary>"/Name/Name": the element's names up to the module, as the old layout lambda printed them.</summary>
    private static string PathOf(FlowMembers m, object element)
    {
        var path = string.Empty;
        for (var e = element; e != null && !m.ESpace.IsInstanceOfType(e); e = m.Parent.GetValue(e))
        {
            path = "/" + (Reflect.TryGet(e, "Name") as string ?? "?") + path;
        }

        return path;
    }
}
