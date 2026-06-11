#!/usr/bin/env python3
"""
Post-process XsdDataContractExporter output into a lean pedagogical XSD.

Algorithm (generalized; no per-type rules):
  1. Keep only the no-namespace schema fragment.
  2. CDataString -> xs:string (DataContract IXmlSerializable quirk).
  3. Strip xmlns:qN aliases and DROP any element whose type references qN:*
     (these point at namespaces we discard; typically string-array helpers
     for rarely-used properties like generic TypeParameters).
  4. Rename DataContract collection wrappers:
     KeyedObjectCollectionOfXXXX<hash> / ArrayOfXXXX<hash> -> XXXXCollection.
  5. Drop <xs:annotation> blocks (DefaultValue boilerplate noise).
  6. Drop nillable="true" (everything is nillable; not pedagogical).
  7. BFS prune to types reachable within DEPTH hops from the root, plus
     transitively-referenced simpleTypes (enums) at any depth.
  8. Collapse empty <xs:element>...</xs:element> to self-closing.
"""
import sys, re
import xml.etree.ElementTree as ET
from xml.dom import minidom

XS = "http://www.w3.org/2001/XMLSchema"
ET.register_namespace("xs", XS)

def main(schema_dir, root_type, depth=3):
    no_ns_path = None
    for i in range(20):
        p = f"{schema_dir}/schema-{i}.xsd"
        try:
            with open(p, "r", encoding="utf-8-sig") as f:
                head = f.read(2000)
            if 'targetNamespace=' not in head:
                no_ns_path = p; break
        except FileNotFoundError:
            continue
    if not no_ns_path:
        print("ERROR: no no-namespace schema found", file=sys.stderr); return 1

    with open(no_ns_path, "r", encoding="utf-8-sig") as f:
        raw = f.read()

    # Pre-parse text fixes.
    raw = re.sub(r'\s+xmlns:q\d+="[^"]*"', '', raw)
    raw = re.sub(r'type="q\d+:CDataString"', 'type="xs:string"', raw)
    raw = re.sub(r'\s+nillable="true"', '', raw)

    tree = ET.ElementTree(ET.fromstring(raw))
    root = tree.getroot()

    # Drop elements whose type is still qN:* (unresolvable after stripping aliases).
    def drop_dangling(node):
        for parent in node.iter():
            for child in list(parent):
                if child.tag == f'{{{XS}}}element':
                    t = child.get('type','')
                    if re.match(r'q\d+:', t):
                        parent.remove(child)
    drop_dangling(root)

    # Rename collection types.
    COLL_RE = re.compile(r'^(?:KeyedObjectCollectionOf|ArrayOf)([A-Z][A-Za-z0-9]+?)([A-Za-z0-9]{6})$')
    rename_map = {}
    for child in list(root):
        if not child.tag.endswith('}complexType'): continue
        n = child.get('name','')
        m = COLL_RE.match(n)
        if m:
            item = m.group(1)
            new_name = item + 'Collection'
            if new_name not in rename_map.values():
                rename_map[n] = new_name
    if rename_map:
        text = ET.tostring(root, encoding='unicode')
        for old, new in rename_map.items():
            text = re.sub(r'\b' + re.escape(old) + r'\b', new, text)
        root = ET.fromstring(text)

    # Strip annotations.
    def strip_anns(el):
        for child in list(el):
            if child.tag == f'{{{XS}}}annotation':
                el.remove(child)
            else:
                strip_anns(child)
    strip_anns(root)

    # Index types + elements.
    types = {}; elements = {}
    for child in list(root):
        tag = child.tag.split('}',1)[-1]
        n = child.get('name')
        if not n: continue
        (types if tag in ('complexType','simpleType') else elements)[n] = child

    # BFS prune.
    reachable = set()
    start = root_type
    if root_type in elements:
        rt = elements[root_type].get('type','')
        if rt and not rt.startswith('xs:'): start = rt
    queue = [(start, 0)]
    seen_depth = {}
    while queue:
        name, d = queue.pop(0)
        if name in seen_depth and seen_depth[name] <= d: continue
        seen_depth[name] = d
        if name in types: reachable.add(name)
        if d >= depth: continue
        node = types.get(name)
        if node is None: continue
        for el in node.iter(f'{{{XS}}}element'):
            t = el.get('type','')
            if t and not t.startswith('xs:'): queue.append((t, d+1))
        for ext in node.iter(f'{{{XS}}}extension'):
            t = ext.get('base','')
            if t and not t.startswith('xs:'): queue.append((t, d+1))

    # Closure pass: include simpleTypes referenced from any reachable complexType,
    # PLUS every concrete subtype of a reachable base type (xsi:type=Subtype in
    # on-disk XML requires the derived complex type to be in the schema), PLUS
    # the new types' own immediate-reference closure.
    while True:
        added = False
        for name in list(reachable):
            node = types.get(name)
            if node is None: continue
            for el in node.iter(f'{{{XS}}}element'):
                t = el.get('type','')
                if t and not t.startswith('xs:') and t in types and t not in reachable:
                    reachable.add(t); added = True
            for ext in node.iter(f'{{{XS}}}extension'):
                t = ext.get('base','')
                if t and not t.startswith('xs:') and t in types and t not in reachable:
                    reachable.add(t); added = True
        # Pull in subtypes: any complexType whose xs:extension base is reachable.
        for tname, tnode in types.items():
            if tname in reachable: continue
            for ext in tnode.iter(f'{{{XS}}}extension'):
                base = ext.get('base','')
                if base in reachable:
                    reachable.add(tname); added = True; break
        if not added: break

    # Emit.
    new_root = ET.Element(f'{{{XS}}}schema', {'elementFormDefault': 'qualified'})
    if root_type in elements:
        new_root.append(elements[root_type])
    simples = sorted([t for t in reachable if types[t].tag.endswith('simpleType')])
    complexes = sorted([t for t in reachable if types[t].tag.endswith('complexType')])
    for t in complexes: new_root.append(types[t])
    for t in simples: new_root.append(types[t])

    # Collapse empty element close tags.
    out = ET.tostring(new_root, encoding='unicode')
    pretty = minidom.parseString(out).toprettyxml(indent='  ')
    pretty = re.sub(r'<xs:element([^/]*?)>\s*</xs:element>', r'<xs:element\1/>', pretty)
    lines = [l for l in pretty.split('\n') if l.strip()]
    print('\n'.join(lines))
    return 0

if __name__ == '__main__':
    sd = sys.argv[1]; rt = sys.argv[2]; d = int(sys.argv[3]) if len(sys.argv) > 3 else 3
    sys.exit(main(sd, rt, d))
