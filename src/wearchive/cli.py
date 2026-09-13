from __future__ import annotations

import typer

from . import __version__

app = typer.Typer(help="Local-first personal chat archive toolkit.")


@app.command()
def version() -> None:
    """Print the installed version."""
    typer.echo(__version__)


@app.command()
def doctor() -> None:
    """Run basic environment diagnostics."""
    typer.echo("WeArchive environment: OK")


if __name__ == "__main__":
    app()
