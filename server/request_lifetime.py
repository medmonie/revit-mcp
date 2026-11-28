"""Cancellation boundary shared by a tool call and its AnyIO worker context."""
from contextvars import ContextVar
from threading import Event

cancelled: ContextVar[Event | None] = ContextVar("revit_request_cancelled", default=None)


def checkpoint() -> None:
    signal = cancelled.get()
    if signal is not None and signal.is_set():
        raise RuntimeError("The MCP request ended before this step was dispatched.")


def ended() -> bool:
    """True once the MCP request that started this work was cancelled or has returned."""
    signal = cancelled.get()
    return signal is not None and signal.is_set()
