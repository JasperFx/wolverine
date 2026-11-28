# dcb.events Playground fixture (GH-6004)

`prevent-record-duplication.dcb.json` is one of the nine dcb.events example models Bobcat vendors for its
`DcbPlaygroundReader` (JasperFx/bobcat#849, `src/Bobcat.EventModel.Tests/Fixtures/Import/vendored/dcb/examples/`),
copied byte for byte so `write-specs` is proven against a model that reader lowers itself, with no board behind it.
**It is untrusted data**: the tests read it; nothing executes it, and nothing here is compiled.

| File | Source | Licence |
|---|---|---|
| `prevent-record-duplication.dcb.json` | https://dcb.events/examples/prevent-record-duplication/ -- the model behind the worked example, as the page's "open in the playground" link carries it (`#model=`, gzip + base64url), decoded and pretty-printed. Website source https://github.com/dcb-events/dcb-events.github.io, commit `56378a4175022b3cccd69ac05d2f44fe7cfda655` (2026-11-27) | **CC BY-SA 4.0** (the site's `LICENSE` puts everything under `docs/` under it); the Playground's own code and schema are MIT (`LICENSE` beside this file, copied from https://github.com/dcb-events/dcb-playground) |

<!-- JEREMY: the same licence question Bobcat's vendored README raises. The example model is decoded from a
dcb.events documentation page, which is CC BY-SA 4.0, not MIT. It is a test input, attributed above, never shipped in
a package, but a share-alike licence inside an MIT repository is your call; the MIT alternative is one of the
Playground repository's own fixtures/*.dcb.json. -->
