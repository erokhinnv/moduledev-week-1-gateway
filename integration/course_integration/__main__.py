"""``python -m course_integration <entrypoint>``.

The main process of every container is the Python interpreter itself (no shell or wrapper),
so the entrypoint name is an argument, not a separate executable.
"""

from __future__ import annotations

import sys
from typing import Callable, Sequence

from .config import ConfigError
from .logs import Logger

EXIT_CONFIG = 2
EXIT_USAGE = 64


def _entrypoints() -> dict[str, Callable[[], int]]:
    # Imported lazily: the adapter must start without the PostgreSQL driver being touched.
    def dispatcher() -> int:
        from .dispatcher import main

        return main()

    def adapter() -> int:
        from .adapter import main

        return main()

    def reconciler() -> int:
        from .reconciler import main

        return main()

    return {
        "outbox-dispatcher": dispatcher,
        "receipt-adapter": adapter,
        "inbox-reconciler": reconciler,
    }


def main(argv: Sequence[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    entrypoints = _entrypoints()
    if len(args) != 1 or args[0] not in entrypoints:
        print(
            "usage: python -m course_integration {" + ",".join(sorted(entrypoints)) + "}",
            file=sys.stderr,
        )
        return EXIT_USAGE
    try:
        return entrypoints[args[0]]()
    except ConfigError as error:
        Logger(args[0]).error("configuration.invalid", reason=str(error))
        return EXIT_CONFIG


if __name__ == "__main__":
    raise SystemExit(main())
