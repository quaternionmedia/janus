#!/usr/bin/env python3
"""Entry-point shim for the Janus controller.

The actual implementation lives in the janus_router package. This file
exists so the existing systemd unit (`ExecStart=... uv run
input_router.py`) keeps working without a path change.
"""
from janus_router.main import run


if __name__ == "__main__":
    raise SystemExit(run())