# Companion agent image. Build from the agent/ folder:
#   docker build -t sapienheim-agent agent/
# Runs on a private Docker network next to the Valheim server; see deploy/README.md.
FROM python:3.12-slim

COPY --from=ghcr.io/astral-sh/uv:latest /uv /usr/local/bin/uv

WORKDIR /app
ENV UV_LINK_MODE=copy UV_COMPILE_BYTECODE=1 PYTHONUNBUFFERED=1

# Dependencies first (cached unless the lock file changes), then the code.
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project
COPY companion_agent ./companion_agent
RUN uv sync --frozen --no-dev

# Inside the container, listen on all interfaces of the private network. Don't publish 7777 to the internet.
ENV AGENT_HOST=0.0.0.0 \
    AGENT_PORT=7777 \
    AGENT_DASHBOARD_HOST=0.0.0.0 \
    AGENT_DASHBOARD_PORT=7778 \
    AGENT_DATA_DIR=/data

RUN useradd --create-home --uid 1000 agent && mkdir -p /data && chown agent /data
USER agent
VOLUME ["/data"]
EXPOSE 7777 7778

# Secrets come from the environment: ANTHROPIC_API_KEY and AGENT_TOKEN.
CMD ["/app/.venv/bin/python", "-m", "companion_agent.main"]
