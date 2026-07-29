#!/usr/bin/env python3
"""
xUnit v2 -> v3 source codemod for the Wolverine test suites.

Handles the four mechanical breaks that account for essentially all of the churn:

  1. IAsyncLifetime.InitializeAsync   : Task -> ValueTask   (all modifier combinations)
  2. IAsyncLifetime.DisposeAsync      : Task -> ValueTask   (v3's IAsyncLifetime : IAsyncDisposable)
  3. Task.CompletedTask returned from those two members -> ValueTask.CompletedTask
  4. `using Xunit.Abstractions;` -> `using Xunit;` (ITestOutputHelper moved), deduped so that
     CS0105 does not trip TreatWarningsAsErrors.

Usage:  python3 xunit3_codemod.py <dir> [<dir> ...]
"""
import re
import sys
import pathlib

# `public virtual async Task InitializeAsync(` and every other modifier ordering.
MODS = r'(?:(?:public|protected|internal|private|virtual|override|sealed|new|async)\s+)*'
SIG = re.compile(rf'(\b{MODS})Task(\s+(?:InitializeAsync|DisposeAsync)\s*\()')

# Expression-bodied  `=> Task.CompletedTask;`  on one of those members.
EXPR = re.compile(
    r'((?:InitializeAsync|DisposeAsync)\s*\(\s*\)\s*=>\s*)Task\.CompletedTask')


# Explicit interface implementations. In v3 DisposeAsync is inherited from IAsyncDisposable,
# so an explicit `IAsyncLifetime.DisposeAsync` no longer names the declaring interface.
EXPLICIT_DISPOSE = re.compile(rf'(\b{MODS})Task\s+IAsyncLifetime\.DisposeAsync(\s*\()')
EXPLICIT_INIT = re.compile(rf'(\b{MODS})Task\s+IAsyncLifetime\.InitializeAsync(\s*\()')


# Expression-bodied lifetime member returning a non-ValueTask expression.
EXPR_BODY_TASK = re.compile(
    rf'^(\s*(?:(?:public|protected|internal|private|virtual|override|sealed|new)\s+)*)'
    rf'(ValueTask\s+(?:InitializeAsync|DisposeAsync)\s*\(\s*\)\s*=>\s*)'
    rf'(?!ValueTask\.CompletedTask)(?!await\b)(.+?;)\s*$',
    re.M)

# Block-bodied lifetime member declaration, not already async.
_BLOCK_DECL = re.compile(
    r'^(\s*)((?:(?:public|protected|internal|private|virtual|override|sealed|new)\s+)*)'
    r'(ValueTask\s+(?:InitializeAsync|DisposeAsync)\s*\(\s*\)\s*)$')


def _async_block_bodied(text: str) -> str:
    """Make a block-bodied lifetime member async when it returns a foreign Task."""
    lines = text.splitlines(keepends=True)
    out, i = [], 0
    while i < len(lines):
        m = _BLOCK_DECL.match(lines[i].rstrip('\r\n'))
        if not m or 'async' in m.group(2):
            out.append(lines[i]); i += 1; continue

        # Collect the member body by brace depth.
        j, depth, started, body = i + 1, 0, False, []
        while j < len(lines):
            depth += lines[j].count('{') - lines[j].count('}')
            started = started or '{' in lines[j]
            body.append(j)
            if started and depth <= 0:
                break
            j += 1

        returns = [k for k in body
                   if re.search(r'\breturn\s+(?!ValueTask\.CompletedTask)\S', lines[k])]
        if returns:
            out.append(f'{m.group(1)}{m.group(2)}async {m.group(3)}\n')
            for k in range(i + 1, j + 1):
                lines[k] = re.sub(r'\breturn\s+(?!ValueTask\.CompletedTask)', 'await ', lines[k], count=1) \
                    if k in returns else lines[k]
                out.append(lines[k])
            i = j + 1
            continue
        out.append(lines[i]); i += 1
    return ''.join(out)


def convert(text: str) -> str:
    text = EXPLICIT_DISPOSE.sub(r'\1ValueTask IAsyncDisposable.DisposeAsync\2', text)
    text = EXPLICIT_INIT.sub(r'\1ValueTask IAsyncLifetime.InitializeAsync\2', text)
    text = SIG.sub(r'\1ValueTask\2', text)
    text = EXPR.sub(r'\1ValueTask.CompletedTask', text)

    # `return Task.CompletedTask;` inside a now-ValueTask Initialize/Dispose body.
    out, in_member, depth = [], False, 0
    for line in text.splitlines(keepends=True):
        if re.search(r'ValueTask\s+(?:InitializeAsync|DisposeAsync)\s*\(', line):
            in_member, depth = True, 0
        if in_member:
            depth += line.count('{') - line.count('}')
            line = line.replace('return Task.CompletedTask;',
                                'return ValueTask.CompletedTask;')
            if depth <= 0 and ('}' in line or ';' in line):
                in_member = False
        out.append(line)
    text = ''.join(out)

    # A non-async lifetime member that hands back somebody else's Task no longer converts,
    # because the member now returns ValueTask. Make it async and await instead.
    #
    #   public ValueTask DisposeAsync() => Cleanup();          -- expression-bodied
    #   public ValueTask InitializeAsync() { return Setup(x); } -- block-bodied
    text = EXPR_BODY_TASK.sub(
        lambda m: m.group(1) + 'async ' + m.group(2) + 'await ' + m.group(3), text)
    text = _async_block_bodied(text)

    # ITestOutputHelper moved from Xunit.Abstractions to Xunit.
    if 'using Xunit.Abstractions;' in text:
        has_xunit = re.search(r'^using Xunit;\s*$', text, re.M) is not None
        text = re.sub(r'^using Xunit\.Abstractions;\s*\n',
                      '' if has_xunit else 'using Xunit;\n', text, flags=re.M)
    return text


def main(roots):
    changed = 0
    for root in roots:
        for path in pathlib.Path(root).rglob('*.cs'):
            if '/obj/' in str(path) or '/bin/' in str(path):
                continue
            original = path.read_text()
            updated = convert(original)
            if updated != original:
                path.write_text(updated)
                changed += 1
    print(f'{changed} file(s) rewritten')


if __name__ == '__main__':
    main(sys.argv[1:] or ['.'])
