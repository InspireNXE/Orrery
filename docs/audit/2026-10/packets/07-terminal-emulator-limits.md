# Packet 07: Fix two out-of-range writes in the terminal emulator, cap what a host can make it allocate or repeat, and clean up pasted and saved text

Rating: Mechanical. Findings: S-10 (emulator part), S-11, S-12, S-13, S-20, S-21. Depends on: nothing.
Runner: `packet-sonnet-medium` (Sonnet, medium effort).

## Rules
- Work in your worktree branch, based on `main`. Stage only files you changed, by explicit path; one commit.
- All changes are in `src/Orrery.Core/Terminal/` plus tests in `tests/Orrery.Core.Tests/Terminal/TerminalEmulatorTests.cs`.
  Write escape sequences in tests as `\u001b`, `\r\n` escapes, never literal control characters or real newlines inside
  a string literal (that broke the build before).
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Core.Tests --filter "FullyQualifiedName~Terminal"`
  while working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
Every SSH or Telnet host feeds this parser, and a fault inside `Feed` kills a Telnet session's reader (packet 08
contains that). Each item below was reproduced by a verifier against a copy of these files.

- **S-10.** With auto-wrap off (`ESC[?7l`, mode at `TerminalEmulator.cs:852`), a double-width character printed in the
  last column writes `line.Cells[_cx + 1]` at `:551`, one past the end. The wrap branch (`:523-533`) only runs when
  auto-wrap is on, and the `Columns < 2` guard at `:535` can never be true. Repro: `ESC[?7l ESC[1;80H` then `中` on an
  80-column terminal.
- **S-11.** `Resize` (`:1012-1037`) resizes the screens before the scrollback (`:1020-1022`). When the screen grows,
  `ResizeScreen` pulls `_scrollback[^1]` back in (`:1063-1070`) at its old width, after the screen lines were resized.
  Later writes index past those short lines: `ClearWideAt` (`:566-578`), DECALN (`:375-380`), DCH (`:747-754`), and an
  ordinary `clear` followed by a long line. Repro: 80x24, print 40 lines, `Resize(120, 30)`, then `ESC[1;100Hx`.
  Maximizing, full screen, collapsing a pane or Ctrl+- all grow both dimensions in one resize.
- **S-12.** `_intermediates` (`:47`) grows without limit in the escape-intermediate (`:275-279`) and CSI (`:407-411`)
  states. 200 MiB of spaces after `ESC (` grew the heap by 401 MiB, and `StringBuilder.Clear` kept the capacity.
- **S-13.** REP (`CSI n b`, `:481`) repeats up to 65535 times (the parameter cap at `:390`), and `Feed` (`:170-195`)
  holds `SyncRoot` for the whole chunk while the UI renders under the same lock. A 64 KiB chunk of `x ESC[65535b` held it
  for 4.5-5 s, stalling every tab.
- **S-20.** `TerminalKeys.Paste` (`TerminalKeys.cs:109-114`) removes `ESC[201~` once, so `ESC[2` + `ESC[201~` + `01~`
  re-forms the end marker, and text after it runs as typed commands (pastejacking from a web page). The comment says it
  strips anything that could end the bracket early.
- **S-21.** Code points U+0080-U+009F print as width-1 cells (`:264-268`, `CharWidth.Of` at `:1089-1110`) and come out
  verbatim in `GetText`/`AllText` (`:101-157`) and so in Copy and Save output. U+009B is CSI: `cat`-ing a saved log in a
  terminal that honors C1 in UTF-8 runs it.

## Changes
1. S-10: in `Print`, when auto-wrap is off and a width-2 character doesn't fit (`_cx + 1 >= Columns`), behave like xterm:
   don't write the continuation cell past the margin. Either place the character one column left, or print a
   replacement width-1 character; pick xterm's behavior if you can confirm it, otherwise the replacement, and say which.
   Remove the dead `Columns < 2` test.
2. S-11: resize each line as it's pulled back from scrollback (or resize scrollback before the screens), so every
   line on screen has `Cells.Length == Columns`. Add a debug assertion of that invariant after `Resize`.
3. S-12: stop appending to `_intermediates` after a small limit (VT500 allows 2; use something like 4) and mark the
   sequence as invalid so it's ignored at its final byte.
4. S-13: clamp REP's count to `Columns * Rows` (repeating more than one screenful has no visible effect beyond
   scrolling). Also restructure `Feed` to take `SyncRoot` per slice of the input (the existing `take` loop is a natural
   boundary) rather than for the whole call, keeping title, bell and reply handling correct across slices.
5. S-20: in `Paste`, remove every ESC (U+001B) and other C0 controls except tab, CR and LF, before wrapping in the
   bracket markers. Do it in both bracketed and plain modes. Update the doc comment to say what's removed.
6. S-21: drop C1 code points at `Print` through the existing width-0 early return (`:515-518`), so they never reach a cell.
7. Tests in `TerminalEmulatorTests.cs`: one per item, using the repros above, plus:
   - after S-11's resize, every row's length equals `Columns` and the repro sequence doesn't throw;
   - S-12: feeding `ESC (` plus 1 MiB of spaces then `B` leaves memory use sane (assert the parser state, not GC numbers)
     and the following text prints normally;
   - S-13: `x ESC[65535b` on 80x24 produces at most one screenful of `x` in scrollback;
   - S-20: replace the weak existing paste test (around `:269`, which only checks one index) with one that checks the
     result contains exactly one `ESC[201~`, at the end, for the nested-marker input;
   - S-21: `AllText` after printing U+009B and U+009D contains neither.

## Verify
- Each new test fails on `main` and passes after.
- `dotnet test Orrery.sln` green.

## Do not touch
- `TelnetSession` (packet 08) and `TerminalView` (packets 06 and 16).
- Hidden (SGR 8) text and bidi characters in copy and save: that's ordinary terminal behavior and out of scope.
