# Layout — the standard layout of logic flows

**Read this only when changing the layout** (`bridge/src/Layout/FlowLayout.cs`). Agents never
need it: they call the bridge's `layoutFlows`, or a project's hook calls it after each merge.
All an author needs is to set no positions and to write notes as one-liners linked to their node.

`FlowLayout.Arrange` moves the nodes of every logic flow of a module — server, service, client,
screen, data and REST actions — onto Service Studio's grid, and wraps and places their comment
notes. It moves nodes and wraps note text, nothing else, and it knows nothing of Service Studio:
it works on a plain flowchart (`bridge/src/Layout/Flow.cs`) of each node's kind, position and
targets in branch order, and each note's text and links. The bridge's `layoutFlows`
(`bridge/src/Studio.Layout.cs`) reads that flowchart from the open module through the Model API,
runs the layout, and writes the positions and texts that changed back as one undoable command.
The positions and line breaks are computed, not chosen by anyone: the same flow always comes out
the same, whoever runs it. That is why notes are authored as one-liners.

A dry run (`layoutFlows` with `dryRun`) changes nothing and reports what would change: the counts
(`FlowsScanned`, `FlowsChanged`, `NodesMoved`, `NotesWrapped`) and, per flow, the nodes and notes
that would move out of all of them, and the notes whose text would be wrapped:

```
/Customer_GetByEmail: 2 of 6
/Order_Submit: 9 of 23, 1 wrapped
```

`flows` takes flow **paths** or plain **names**, comma-separated: a top-level action's path is
`/ActionName`; a name matches every flow whose path ends in it. An entry that matches nothing
stops the run, which then lists every flow's path.

The layout began as a team's Model API lambda, run through `applyModelApiCode` before every
merge. Ported to the plain flowchart, it reproduces that lambda exactly: on 20,868 fixture and
random flows (388,903 nodes), no position and no note text differed (2026-11-22).

## What it produces

So that a result can be explained, not reproduced by hand:

- **Grid.** Service Studio's own snap grid, so a node the layout places
  and a node a developer drags line up: columns and rows are 12800/7 units
  apart (about 1828.57 — eight grid dots). Columns start at X = 3200; rows
  are Y = 914, 2743, 4571, … `Start` is the exception: it stays at
  (3196, 800), where Service Studio creates it, so the first node below it
  is at Y = 2743. The only nodes between two rows, half a row (four grid
  dots) off, are those of a loop's ring drawn at half height (*For Each
  loops* below).
- **Main path.** Straight down from `Start`.
- **Branches.** At an **If**, one target continues straight
  down and the other goes one column to the right, **starting on that
  node's own row**: a `Raise Exception` goes to the right; of the branches
  left, the one with the **most nodes of its own** (nodes no other branch
  of that If reaches) continues down, so a short branch, such as an early
  exit, is the one to the right; a branch that is a single end node loses to any
  other, and on a tie the last one continues down (`False`).
  A branch that **rejoins** the path the other branch goes to directly is
  a detour: it goes to the right and the join stays in the column. That
  only holds when the join really stays there: a join that a node already
  placed further left also leads to goes in that node's column, so it
  doesn't stop the flow going down: an If after that join goes below it,
  not beside it. In the same way, a
  branch that rejoins the other as a **plain chain** — every node of it
  leads to exactly one node, nothing else leads into it, none is a For
  Each, and the last leads to a node the other branch also reaches —
  continues down however short it is, and the other branch goes to the
  right. That branch starts on the If's own row and ends beside the join,
  which stays in the column below the chain. Otherwise the chain would sit
  to the right and reach back to a join far below, with a long diagonal
  across the other branch. So an If whose one branch is a
  single Assign that rejoins sends that Assign down, and its other
  branch, with its guards and its loop, goes to the right and comes back
  into the join from beside it. Of two such
  chains, the shorter continues down (on a tie, the last). The
  only side branch of a node **runs along that node's row** instead of
  down its column when it is a plain chain that ends separately — every
  node of it leads to at most one node, nothing else leads into it, none
  is a For Each, and the last is an `End` or `Raise Exception` — and the
  cells to the right are free: `If → Assign → End` reads left to right.
  A branch with an If, a join or a loop in it goes down as before.
- **Switches.** A Switch **fans its branches out side by side** on the row
  below it, one column each from the Switch's own column rightwards. A
  branch that takes more than one column (an If with a side branch, a
  nested Switch) pushes the next one further right, but the fan's first
  nodes stay level. The order: first the branches that **rejoin** — lead
  to a node another branch also leads to — in branch order (when none
  does, the one with the most nodes of its own, where the flow goes on);
  then the other branches, in branch order; last the ones that **end
  separately**, a plain chain to an `End` or `Raise Exception` as above.
  One branch that ends separately goes **aside** instead: to the right on
  the Switch's own row, as at an If, running along that row when it is a
  chain of several nodes — the shortest such branch (on a tie, the last),
  only when another branch goes on and only when it is no longer than the
  longest branch that does. In a Switch with four cases that rejoin and
  `Otherwise → Raise Exception`, the `Raise Exception` sits beside the
  Switch and the four cases sit level below it, rejoining at the node
  under the first. A branch that goes straight from the Switch
  to the join the others rejoin is a detour: the join stays in the
  Switch's column and the fan starts one column to the right — on the
  Switch's own row when it has a single branch, as at an If.
- **Joins.** A node several branches lead to goes in the leftmost column of
  those branches: below the nodes of its own column, and level with the
  last node of a branch in the column beside it — the standard spacing,
  kept even when one of its connectors then runs close past another node.
  A connector into a join must never run exactly through a node — not even
  through another branch of the same join, where the two lines would look
  like one. The branch it comes from moves down instead, a row at a time,
  as long as it stays above the join, nothing already placed leads on
  from it, it isn't part of a loop's block, and its new cell and the
  connectors into it are clear: of two branches side by side, the right
  one's last node goes down to the left one's row, beside it, so its line
  to the join passes between nodes.
  Only when that can't clear the line is the join pushed further down.
- **Ends where branches join** (an `End` or `Raise Exception` several
  branches lead to) close up instead of stretching down:
  - the **last node of a branch beside the end**, one column left or
    right, moves down **level with it**, so its connector runs straight
    along the row — when it leads only to the end, isn't part of a loop's
    block, every connector into it comes straight down its column and
    nothing is below it there, and its new cell and longer connectors
    stay clear of the connectors drawn already;
  - an end whose branches come from **three columns** goes in the
    **middle one**, under the middle branch, with the other two level
    with it on either side — when that column is free down to there, a
    branch in it is its lowest node, and each branch on either side is
    level already or can move down as above. In a flow that returns a
    cached access token, refreshes it, or fetches a new one, the three
    `AccessToken` Assigns end on one row with `End` between them, with no
    diagonal, a row shorter than with `End` under the leftmost branch.

  A join that leads on keeps the rules above: it stays in its leftmost
  column so the flow goes on down it, and the cell beside it is left for
  its own side branches.
- **For Each loops.** A For Each and the nodes only its cycle leads to
  (its body, their `Raise Exception`s, nested loops) are laid out together
  as one block, to the right of the For Each. No shape is prescribed: the
  layout searches for the arrangement with the **lowest energy**. A
  connector costs by how far sideways it goes, how long it is, whether it
  flows upwards, whether it leaves or enters an If or Switch diagonally, and — for a
  For Each's `Cycle` connector, or the connector into the cycle of a
  loop built with an If — whether it goes down; every
  pair of consecutive loop connectors costs by how sharply the path bends;
  crossings cost extra; and each cycle earns a reward for the area it
  encloses, up to a cap, which keeps a loop inflated instead of folded
  flat. What comes out:
  - a **short loop closes into a ring** around the For Each — out along
    the top, down the far side, back along the bottom;
  - a ring is drawn at **half height** — its rows become half rows — when
    every node then stays at least a row from the others in its column
    and no connector comes within half a cell of a node it wasn't that
    close to before. So a For Each with three nodes in its body is a flat
    diamond: the first sits half a row above the For Each's row, the
    second on it and the third half a row below. A ring with nodes in
    the next row of the same column (four nodes or more, usually) keeps
    whole rows. The cells around it are the same either way: a node half
    a row off still keeps the rows above and below it in its column clear;
  - the **`Cycle` connector leaves upwards or sideways**: going down costs
    extra, so it does only when that is clearly better. A loop of
    For Each → Aggregate → If (True back to the For Each) has the Aggregate
    above the If, not below it;
  - an **If inside a loop** sends its connectors straight up, down or
    sideways, so it sits out on the far side of the ring, with a straight
    connector of up to two cells to or from the For Each. It is entered
    straight too: For Each → `Filter` → `Found?` runs out along the top of
    the ring, not up to `Filter` and diagonally back down into `Found?`;
  - branches inside a loop that both return become two lobes, above and
    below the For Each's row;
  - a **For Each inside a loop** is laid out as one on its own: its body
    goes to its right, and only the path through it (in from the node
    before it, on to the node after it) and its own cycle count as bends,
    never the way in turning into its `Cycle`. So the loop it is in goes
    on straight down past it: an Aggregate → the For Each over its rows →
    an inner For Each → the node after it stay in one column, with each
    inner ring to the right.
    Before, the inner For Each stepped a column right to use the cell
    above it for its ring, and its ring followed the way in down;
  - a **long loop** (more than about eight nodes on its cycle) keeps
    flowing down in a column with one long connector back, because a ring
    that tall would make half the body run upwards;
  - an end node (`Raise Exception`, `End`) sits directly to the right of
    the node that leads to it, and a plain chain that ends separately
    from an If or Switch of the loop runs along that node's row to the
    right, as outside a loop: `Attempts left?` → an Assign → an Execute
    Action → `Raise Exception`.

  The For Each is placed on the first row where the whole block fits in
  free cells, so a ring may reach up beside the nodes above it.
- **Loops built with an If.** A connector that loops back to a node that
  isn't a For Each — the loop's **head** — makes a loop too: its cycle
  (every node that leads back to the head without passing through it,
  nested loops included) and the nodes only that cycle leads to are laid
  out as one block by the same search, so the connector back never runs
  up the column through the loop's own nodes. The loop is left from its
  **pivot**: the node of the cycle whose connector out of it leads to the
  most nodes — not to a `Raise Exception`; on a tie, the one nearest the
  head. When the pivot is the head (an If at the top of the loop), the
  block goes to its right, as for a For Each. Otherwise the pivot goes in
  the head's column below it, the nodes that lead only from one to the
  other may go between them, and every other node of the loop goes to the
  right. Either way the flow goes on straight down from the pivot after
  the loop. In a loop that reads a list a page at a time, the node that
  reads a page → `Found?` stay in the column with `False` going on below
  `Found?`, and `True` rings out to the right, up beside the paging step
  and back down into the node that reads the page. In a loop whose If is at the bottom,
  the nodes between the head and the If step out to the right instead,
  because the connector back up the column would run through them.
- **No overlaps.** A node with a side branch is pushed below everything
  already placed in the columns to its right — unless its only side branch
  is a plain chain, ending separately or rejoining, and no node to its
  right still leads to a node not placed yet. Then the chain needs only
  its own cells: the node goes on the first row where the next column is
  free from there down (or, for a chain that runs along the row, where its
  cells on the row are free), clear of drawn connectors. So the node after
  a loop **tucks in right below the loop's nodes in the next column**
  instead of below the whole loop: an If after the loop and its
  `Raise Exception` sit right below the loop's last If, beside the loop's
  lower right part. A node is also pushed down
  until **no connector runs through a node it doesn't belong to, or within
  half a cell of one** — this is what moves the node after a long loop
  clear of the loop's connector back. A node's own connectors push it at
  most 6 rows, and never when they run straight along its column, which
  moving down can't clear. A join is the exception: its own connectors
  push it down only when they would run exactly through a node (*Joins*
  above). So is the first node of each branch of a Switch's fan: a
  connector, its own or one already drawn, pushes it down only when one
  would run exactly through a node, because the fan's connectors pass
  close to each other's nodes by design. Connectors may still cross each
  other.
- **Exception handlers** start their own flow on `Start`'s row, two columns
  to the right of everything placed before them, in their existing
  left-to-right order. Nodes that nothing leads to are treated the same
  way, after the handlers.
- **Note text.** Service Studio shows a note with exactly the line breaks
  its text has and cuts a longer line off with "…", so the text is first
  **unwrapped**, then **wrapped at 100 characters**:
  - every line is trimmed at the end; a single line break is unwrapped —
    the lines around it are joined with a space — and only an empty line
    (two or more line breaks in a row) is kept, as the break between
    paragraphs; empty lines at the start and end go, and runs of spaces
    or tabs inside a paragraph become one space. So a note someone wrapped
    by hand at any width comes out the same as its one-line version;
  - each paragraph then breaks at a space — outside double quotes when
    that keeps the line at least half full, so a quoted suggestion stays
    in one piece — with continuation lines indented like the paragraph's
    first line, past its leading spaces and a `- `, `* ` or `1. ` marker.
    A word longer than a line (a URL) is never split.

  A list written one item per line becomes one paragraph: separate items
  with empty lines to keep them apart. A text already in this form is left
  as it is, so a second run changes nothing.
- **Note size.** A note's position is its top-left corner. It is taken
  to be 320 + 68 units per character of its longest line wide (a
  100-character line is about 7100, nearly four columns) and 320 + 175
  units per line tall — measured on Service Studio's rendering, a little
  generous.
- **Node size.** A node's position is its icon's top-left corner, not its
  centre: the icon is taken as 430 square, and the node with its label as
  1600 wide around the icon's centre, from 750 above that centre to 250
  below it — measured on Service Studio's rendering of a two-line label. A
  note's link is drawn from the note's centre to the icon's centre.
- **Labels.** A one-line note of up to 20 characters goes in the left
  margin of its flow, 2500 left of the flow's first column — X = 700 for
  the main flow. A label linked to a node of an exception handler flow
  (the second `Assert.` of a negative test) goes in that handler's own
  margin, so its connector doesn't run across the main flow. A linked
  label is centred on the nodes it links to, where their pull balances
  (*Traction* below): on its node's icon when it links to one, halfway
  between two. An unlinked one starts on the row of the nearest node at
  or above where it was. Labels that would overlap are stacked downwards.
- **Every other note settles like a body in a physical simulation.** It
  has an invisible margin of 400 all round it: no node — icon and label —
  may be inside it. Other notes may be inside it, but no closer than 250
  to the note itself.
  - **Traction.** A note linked to nodes is pulled towards their icons'
    centres, as if its links were under tension: each link pulls the
    note's nearest edge towards its node 16 times as hard as it pulls the
    note's centre, so a note linked to one node is centred on its row, and
    a note linked to several settles where their pulls balance, centred
    between them. Its **rest point** is where that pull is least, outside
    every node's margin, with every link clear of the other nodes — the
    line from the note's centre to its node passes no other node's icon or
    label closer than 150 — and the note no closer than 150 to any
    connector. So a note never draws its link across the node beside its
    own: where a `Raise Exception` sits right of the If the note is about,
    the note sits right of it, shifted down until its link passes between
    that `Raise Exception` and the next row's (above can't work: the label
    is above the icon). Where no place keeps the links clear, a link may
    cross a node; where no place keeps the note off connectors either, it
    is merely outside every node's margin.
  - **Contacts.** Starting from their rest points, the linked notes of a
    flow move in steps. Each step every note moves an eighth of the way
    back to its rest point; then two notes too close push each other apart
    along the axis where they overlap least, half each, and a note pushed
    into a node's margin, or to where it would lie on a connector or a
    link of it would cross a node, moves the shortest way out — pair by
    pair, until nothing touches. The simulation stops when a step moves
    nothing. Notes linked to the same node end up one above the other.
  - **Notes linked to no node** — to nothing, or only to other notes —
    have nothing pulling them: one stays where it is, unless a node is
    inside its margin, and then it moves the shortest way out. Other notes
    don't move it. They settle before the linked notes, which keep clear of
    them.

  The positions depend only on the flow's nodes, the notes' links and
  their text (and, for a note linked to no node, where it already is), so
  a second run moves nothing. Ties — notes linked to the same nodes — are
  broken by their text (compared character by character, the same on
  every machine), then by where the notes are, never by the order the
  model lists them in, which Service Studio doesn't keep from one snapshot
  to the next. Where they are comes last because the simulation doesn't
  keep their order: ordering by it would make a second run start from
  another order and land elsewhere.

## Known limits

- **Verified on live modules:** server actions with Assign, If, For Each,
  Execute Action, Aggregate, SQL, Raise Exception and comment notes; REST
  test methods in test suite modules, including exception handler
  flows and their `Assert.` labels (2026-10-08). Switch fans:
  a Switch of four branches rejoining and `Otherwise → Raise Exception`,
  the positions read back from
  the live sandbox matching the mock of the Model API exactly, merged,
  and a second run changed nothing (2026-10-15). **TODO:** nested loops and the
  flows of client, screen and data actions go
  through the same code and pass against a mock of the Model API, but have
  not been checked in Service Studio yet. On the first module that has
  them, look at the merge diff and say so in the report. Note unwrapping
  and wrapping: five hand-wrapped notes, merged
  (2026-10-10), plus a probe of the functions in the live sandbox (quoted
  suggestions, list markers, paragraphs, a long URL, CRLF text, tabs and
  double spaces); a second run changed nothing in either. Note placement:
  a live dry run on a flow with eight
  notes — four of 5 to 8 lines, two on one node, one linked to three
  nodes, one unlinked — matching a reference simulation exactly
  (2026-10-10); not merged yet. Loops built with an If: a live dry run on
  a connector module changed only its paging loop, and its positions read
  back from the live sandbox matched the mock exactly (2026-10-19). Dry
  runs of four more modules changed only the two other flows with such a
  loop, both laid out with no connector through a node. Two of the three,
  one with its chain to a `Raise Exception` along the row, were then
  merged and read back as laid out, and a second run changed nothing
  (2026-10-19). Against the mock, on
  370 random flows with such loops of every shape — an If at the top,
  nodes before the If, the If at the bottom, nested in and around For
  Each loops — connectors through a node went from 3793 to 125 and the
  total height down by over a third; 400 flows without one came out
  unchanged, and second runs moved nothing. Entering an If diagonally
  (2026-11-12): against the mock, on 1200 random flows with loops — For
  Each loops and loops built with an If, the If at the head included —
  diagonals into an If or Switch went from 1270 to 689, connectors
  through a node from 11 to 2, crossings and height stayed within 1%,
  connectors neither straight nor diagonal rose by 6%, and second runs
  moved nothing. A rejoining plain chain going down (2026-11-13): against
  the mock, on 600 random flows without loops, crossings went from 1207
  to 837, connectors through a node from 226 to 145, connectors within
  half a cell of a node from 1336 to 1054, height down 3% and width up
  5%; on a score of all four, 148 flows came out better and 1 worse (a
  long diagonal into a join with many branches, as below). On 1200 flows
  with loops, 137 better and 1 worse; second runs moved nothing. Both
  changes were merged into a live module (2026-11-13): four flows moved,
  one (a ring with an If entered straight, a rejoining chain down) went
  from 51 rows and 3
  crossings to 46 and none, the positions read back from the live module
  matched the mock exactly, and a second run changed nothing. A node
  tucking in below a loop (2026-11-13): against the mock, on 2000 random
  flows with loops and 1200 without, 99 flows changed: 96 better on the
  score above, 3 the same, none worse; height down 0.2–0.8% per set,
  crossings and connectors through a node unchanged in total. Without the
  rule that nothing to the right still leads on, 2 flows came out worse:
  a Raise tucked in beside a branch's connector, drawn later, down to its
  join. The 22 hand-drawn fixtures came out unchanged and second runs
  moved nothing. A live dry run changed only one flow, two rows
  shorter. A ring
  at half height (2026-11-13): rows became half rows throughout, so
  against the mock, flows without such a ring came out identical; on
  3500 new random flows with loops (1500 with notes), 11 changed, 3 a row
  shorter and none worse on the score above, with crossings, connectors
  through or within half a cell of a node and note placement unchanged;
  the earlier random sets and the hand-drawn fixtures likewise, and
  second runs moved nothing. A live dry run changed nothing: the
  developer had already drawn such a diamond at half height by hand,
  exactly as the layout now does, which the previous version would have
  undone. A For Each inside a loop laid out as on its own (2026-11-13):
  against the mock, on 2800 random flows with loops, 658 changed: 283
  better on the score above and 117 worse (mostly a row taller, in tangles
  of nested loops), crossings down from 3946 to 3928, height down 0.8%,
  width up 1.4%, connectors through a node from 1 to 2 (in such a
  tangle); 1500 more with notes, 142 better and 58 worse; flows without
  loops unchanged, of the hand-drawn fixtures only a nested loop changed
  (a row shorter), and second runs moved nothing. Keeping the body to the
  right alone left the inner ring upside down (its `Cycle` diving to
  follow the way in); not counting those bends alone let the body slide
  to the left of the inner For Each, and scored worse than before. A live
  dry run changed two flows: 29 of 40 nodes of one, and 1 of 49 of the
  other (a one-node body now right of its For Each, not below it); the
  positions it gave the first matched the mock exactly. Ends where branches
  join (2026-11-16): against the mock, on 6900 random flows with and
  without loops, 40 changed, 7 better on the score above and none worse;
  diagonals went from 18641 to 18589, connectors neither straight nor
  diagonal from 7004 to 6971, connectors through a node from 290 to 289,
  crossings and note placement unchanged; the hand-drawn fixtures and the
  flows exported from a live module came out unchanged, and
  second runs moved nothing. Applied to every join, moving a branch level
  took the cell a join's own side branch needs, and centring under a
  column holding two of the branches ran the upper one's connector
  through the nodes between: more flows came out worse than better. Not
  yet dry-run against a live module.
- **Long connectors can still pass through nodes.** A connector running
  straight down a column to a join below other nodes of that column (an
  early exit from high up), or a long diagonal into a join with many
  branches, can pass exactly through a node: pushing the join further
  down doesn't clear it, so after 6 rows the join stays. Checked on 400
  random flows of nested Ifs, joins, early exits, raises and loops
  (2026-10-10): the old code left as many such passes and, avoiding them,
  pushed some joins hundreds of rows down; the 6-row limit removed every
  such runaway, and second runs moved nothing. On 400 random flows with
  Switches of two to five branches, nested up to three deep (2026-10-15),
  the fan cut such passes from 602 to 61 and the total height by a
  quarter against the layout before it; flows without a Switch came out
  unchanged, and second runs moved nothing. Most passes left are of this
  kind, from a branch of an outer If or Switch; a few run into a For
  Each's block, whose nodes never move to clear one.
- **A wide fan.** Each branch of a Switch gets its own column, so a Switch
  with many branches, or one nested in another's branch, makes the flow
  wide. The connectors to the far branches run close past the fan's
  nearer nodes — the one to the fourth branch of four passes a third of a
  row above the third — and can graze a two-line label.
- **A note's links can still cross things.** When no place keeps a
  note's links clear of the other nodes and the note off every connector
  — about one note in ten in random flows, often one above the flow's
  first rows, where there is no room higher up — its link crosses a node,
  usually a label. A link may also cross another note: two notes on the
  same node stacked one above the other, the lower one's link passing
  through the upper one. Links to other notes, and labels' links, are
  never checked. The sizes of nodes and notes are estimates (a line of
  wide capitals is wider than 68 per character, a short label narrower
  than 1600), which the margins absorb. Checked on 600 random flows with
  1100 linked notes against the mock (2026-11-13): links through a node
  went from 302 to 111, notes on a connector from 6 to 0, the gap between
  a note and its nodes shrank by a third, no two notes overlapped, and
  second runs moved nothing — which the old placement didn't manage on 9
  of 300 flows with several notes on one node. A live dry run placed
  a note on a validation's If below its `Raise Exception`'s row, its link
  clear.
- `layoutFlows` lays out the **whole module** unless `flows` names some, not
  only the flows a change touched: run after every merge, a legacy flow that was
  never on the grid moves after the next merge of that module, as part of the
  Layout Flows step. That is deliberate —
  the standard applies to every flow — but worth a sentence in the report
  the first time it happens on a module.
- A ring makes part of a loop read upwards or right to left: the first node
  sits above the For Each's row and the last ones come back along the
  bottom. That is the price of short connectors.
- A loop of **two nodes** — a node and the If right after it that loops
  back to it, or a For Each with one node in its body — draws its two
  connectors on top of each other: straight connectors between the same
  two nodes can't be told apart, wherever they go.
- A loop built with an If that the search can't lay out as a block — one
  entered from more than one place, say — is laid out by the If rules,
  and its connector back can run up the column through its nodes again.
- A join keeps the standard spacing below its branches, so a connector to
  it can run close past another node — a diagonal from a branch a few
  columns to the right past the node below that branch, or the jump from
  inside a loop's body to the node after the For Each past the body's
  nodes. A node inside a loop's block never moves down to clear one.
- A long loop still has one long connector back to its For Each. Nothing
  sits under it, but it runs beside the loop's own column.
- The loop search keeps the 80 best partial arrangements at each step.
  When none of them leaves a free cell for the next node, it starts again
  keeping 320; only if that fails too is the loop laid out by the If
  rules. It is deterministic, but on an unusual loop it can settle for an
  arrangement that is not the best possible. Report a shape that looks
  wrong; don't hand-tune positions afterwards — the next merge would undo
  them.
- BPT process flows are not logic flows and are not touched.

## Changing the algorithm

`FlowLayout.cs` is the standard. Change it only when its users agree on a different layout, and
then:

- keep it **integer-only** — no floating point anywhere, so the same flow comes out identical on
  every machine; rows are counted in half rows for that, every node of the flow on an even one —
  and **stable**: a second run on its own output must move nothing. A node's position depends on
  the flow's connections, not on where the node was — the only things read from the old positions
  are the left-to-right order of handler flows and the row of an unlinked note. Never let a result
  depend on the order the flowchart lists nodes in: Service Studio doesn't keep it between reads,
  so a tie broken by it flips from one run to the next;
- the look of loops is set by the energy weights at the top of the algorithm (`LATERAL`, `LONG`,
  `STRAIGHT`, `OFFAXIS`, `LONGBACK`, `BACKCAP`, `UP`, `CLIMB`, `DIVE`, `FORKDIAG`, `LEAF`, `BEND`,
  `CROSS`, `PRESSURE`, `AREACAP`): change a weight rather than adding a special case for a shape.
  The current values were tuned so that the first two loops of a reference flow with three loops
  come out exactly as they were drawn by hand. The third was drawn with a diagonal into its If; since `FORKDIAG` also costs a diagonal
  into an If (2026-11-12), it comes out as a tighter ring instead;
- keep the Model API out of `FlowLayout.cs`: a change that needs more from a flow (a node kind the
  layout should treat apart, say) adds it to `Flow.cs` and to the read in `Studio.Layout.cs`;
- compare the layout before and after on many flows, not on one module: flows exported from real
  modules, and random structured flows (if/else, early exits, Switch, For Each, while loops with
  the If at the top, middle or bottom, nesting, notes). Count what got worse — connectors through
  a node, crossings, height and width, notes on a connector — and check that a second run moves
  nothing. Then try it live with `layoutFlows` `dryRun` on a real module, and update *What it
  produces* above in the same change;
- bump the bridge's version (`servicestudio-mcp-oml/BRIDGE.md` § Versioning).
