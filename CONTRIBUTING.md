# Contributing to TinkerCNC

Thanks for helping! TinkerCNC is an independent, community-maintained project; it is not affiliated with any machine
manufacturer. Bug reports, test reports from real machines, documentation fixes and pull requests are all welcome.

## Before you start

- For anything bigger than a small fix, open an issue first so we can agree on the approach.
- Everything can be developed and tested without a machine: the bundled simulator speaks the whole protocol and the
  test suite runs against it. See [`docs/development.md`](docs/development.md).
- If you do have a machine, a short report of what works and what doesn't (model, firmware version, what you tried)
  is one of the most valuable contributions right now.

## License and sign-off

TinkerCNC is licensed under the [Apache License 2.0](LICENSE). By submitting a contribution you agree that it is
licensed under the same license, as described in section 5 of the license, and you certify the
[Developer Certificate of Origin](https://developercertificate.org): that you wrote the contribution yourself or
otherwise have the right to submit it under this license. Certify it by signing off every commit:

```sh
git commit -s     # adds "Signed-off-by: Your Name <you@example.com>"
```

Pull requests whose commits lack a sign-off cannot be merged.

## Pull requests

- Keep a pull request to one change. Small, focused PRs are reviewed quickly.
- Add or update tests (`dotnet test`) and make sure CI passes.
- Say how you tested it: against the simulator, or on which machine (model and firmware version).
- Follow the coding rules below; reviews will point to them.

## Coding rules

- **SOLID, DRY and clean code** are mandatory:
  - one responsibility per class or component (split helpers such as formatting out of base classes);
  - extend through new types instead of editing dispatch code (open/closed);
  - depend on abstractions (`IMachineTransport`, visitors);
  - no copy-pasted members across types (put shared state in a common base or helper);
  - intention-revealing names and small methods.
- **Machine logic belongs in `TinkerCNC.Host` or `TinkerCNC.Core`**, never in the Blazor UI. Components render state
  and forward actions; sequences that must survive a closed browser tab run in the host services.
- **.NET 10 everywhere** (`net10.0`, set centrally in `Directory.Build.props`).
- **No switch cascades for dispatch.** Use polymorphism instead: a typed message per case plus a visitor
  (`IMachineMessage`/`IMachineMessageVisitor`, `IHostMessage`/`IHostMessageVisitor`), strategy objects, or
  a lookup table (`Dictionary<key, factory/handler>`) when mapping a discriminator to a type.
  No `switch` statements or `switch` expressions, and no long `if/else if` chains over a type or kind.
- **Interfaces as top types** for message hierarchies (`IMachineMessage`, not an abstract base record).
  Concrete cases are `sealed record`s implementing the interface; shared helpers go into default interface members
  or static interface methods.
- **No hand-built JSON** (`JsonObject`, `JsonNode`, string concatenation). Define POCO/record contracts and
  (de)serialize them with System.Text.Json and shared `JsonSerializerOptions`.
- **Describe protocol behaviour in plain terms.** Comments and docs say what the machine does (packet layout,
  commands, states) and may cite the machine's open-source firmware. Strings shown to users are our own wording;
  only protocol keywords and file formats (commands, JSON field names, file names) are used verbatim, because a
  compatible client needs them. Never copy text, names, graphics or internals from other vendors' software.
- The repository is public: keep private things (analysis notes, binaries, scratch files, machine addresses and
  credentials) out of it.
