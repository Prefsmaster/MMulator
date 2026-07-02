using System.Runtime.CompilerServices;

// Z80.Disassembler is READ-ONLY toward the core (root CLAUDE.md §2/§3): this grants
// it visibility into a handful of `internal` classification helpers whose logic must
// structurally never drift from the core's (e.g. IsIndexAffected), instead of letting
// the disassembler hand-copy that logic and risk silent divergence. It must not use
// this access to alter core behaviour.
[assembly: InternalsVisibleTo("Z80.Disassembler")]
