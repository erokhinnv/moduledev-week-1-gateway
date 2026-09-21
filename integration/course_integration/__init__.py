"""Python integration perimeter of the course platform.

One package, one image, three entrypoints:

* ``outbox-dispatcher`` - durable Outbox (PostgreSQL) -> provider v0.2.0;
* ``receipt-adapter``   - provider legacy callback -> signed receipt v1 -> generic C# API;
* ``inbox-reconciler``  - fixed PostgreSQL reconciliation boundary.

None of the processes owns domain state: every decision about flow, limit, transition,
retry schedule or final status is taken by PostgreSQL.
"""

__all__ = ["__version__"]

__version__ = "0.3.0"
