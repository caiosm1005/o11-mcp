using System.Collections.Generic;

// The flowchart the layout works on: an OutSystems-style logic flow reduced to what positions
// depend on. Nothing here knows Service Studio; the bridge fills it from a module and writes the
// result back (Studio.LayoutFlows).
#nullable disable
namespace ServiceStudioBridge.Layout
{
    /// <summary>The kinds of node the layout treats differently; every other node is Other.</summary>
    public enum NodeKind { Other, Start, End, Raise, ExceptionHandler, If, ForEach, Switch, Comment }

    /// <summary>A node of a logic flow, or a comment note in it.</summary>
    public sealed class FlowNode
    {
        public NodeKind Kind { get; set; }

        /// <summary>Position of the node's icon (a note's top-left corner), in Service Studio's units.</summary>
        public int X { get; set; }
        public int Y { get; set; }

        /// <summary>
        /// The nodes it leads to, in branch order: If: True, then False; For Each: Cycle, then the
        /// next node; Switch: each condition, then Otherwise; a comment: the nodes it is linked to;
        /// any other node: its connectors' targets. A missing branch is null.
        /// </summary>
        public List<FlowNode> Targets { get; private set; }

        /// <summary>A comment's text; null for other nodes.</summary>
        public string Text { get; set; }

        public FlowNode() { Targets = new List<FlowNode>(); }
    }

    /// <summary>A logic flow: an action's nodes, exception handler flows included.</summary>
    public sealed class Flow
    {
        /// <summary>Where the flow is in the module, such as "/Customer_CreateOrUpdate".</summary>
        public string Path { get; set; }
        public List<FlowNode> Nodes { get; private set; }

        public Flow() { Nodes = new List<FlowNode>(); Path = ""; }
    }

    /// <summary>What a layout run changed, or would change.</summary>
    public sealed class LayoutReport
    {
        public int Scanned { get; set; }
        public int Changing { get; set; }
        public int Moving { get; set; }
        public int Wrapping { get; set; }

        /// <summary>One line per flow that changes: "path: moved of total[, wrapped wrapped]".</summary>
        public List<string> Lines { get; private set; }

        public LayoutReport() { Lines = new List<string>(); }
    }
}
