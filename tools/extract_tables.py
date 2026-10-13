#!/usr/bin/env python3
"""Extracts constant tables from libwebp C sources and prints them as C# ReadOnlySpan properties.

usage: python3 -I tools/extract_tables.py <c-file> <c-name>:<cs-name>:<cs-type> ...
"""
import re
import sys


def extract(source, name):
    m = re.search(r'\b' + re.escape(name) + r'\s*(\[[^=]*\])\s*=\s*\{', source)
    if not m:
        sys.exit(f"table {name} not found")
    start = m.end() - 1
    depth = 0
    for i in range(start, len(source)):
        if source[i] == '{':
            depth += 1
        elif source[i] == '}':
            depth -= 1
            if depth == 0:
                body = source[start:i + 1]
                break
    body = re.sub(r'//[^\n]*', '', body)
    body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
    values = re.findall(r'-?(?:0x[0-9a-fA-F]+|\d+)', body)
    return [int(v, 0) for v in values], m.group(1)


def main():
    path = sys.argv[1]
    source = open(path).read()
    for spec in sys.argv[2:]:
        cname, csname, cstype = spec.split(':')
        values, dims = extract(source, cname)
        print(f"    // port: {cname}{dims} ({path.split('src/')[-1]}), flattened")
        print(f"    public static ReadOnlySpan<{cstype}> {csname} =>")
        print("    [")
        for i in range(0, len(values), 16):
            print("        " + ", ".join(str(v) for v in values[i:i + 16]) + ",")
        print("    ];")
        print()


if __name__ == '__main__':
    main()
