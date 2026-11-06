# Security

## Reporting a vulnerability

Please do not open a public issue for security problems. Use GitHub's private vulnerability reporting
(**Security → Report a vulnerability** on the repository page) or contact a maintainer directly. You will get an
answer within a few days; fixes are published as a new release with a note in `CHANGELOG.md`.

## What to keep in mind when running TinkerCNC

TinkerCNC moves machine tools. Its dashboard has **no login**: whoever can open it can jog the machine, start
programs and change settings.

- By default the dashboard listens on `localhost` only. Opening it to a network (`--urls http://0.0.0.0:5185`)
  gives everyone on that network control of the machine; do this only on a network you trust, and never expose it
  to the internet.
- The machine protocol itself (TCP 2222, UDP 3333) has no authentication either; that is a property of the
  machines, not of TinkerCNC.
- Supported versions: the latest release. Older releases get no security fixes.
