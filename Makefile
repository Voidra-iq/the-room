# The Room — local setup & run (uses dotnet + Godot 4.7 mono)
# Run `make help` for targets.

SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c

ROOT := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))

BLUE := $(shell printf '\033[34m')
GREEN := $(shell printf '\033[32m')
YELLOW := $(shell printf '\033[33m')
RESET := $(shell printf '\033[0m')

.DEFAULT_GOAL := help

# .NET SDK lives here but isn't on the default shell PATH on this machine.
DOTNET_DIR := /usr/local/share/dotnet
DOTNET     := $(DOTNET_DIR)/dotnet
export PATH := $(DOTNET_DIR):$(PATH)

# Godot 4.7 mono editor/runtime (macOS app bundle).
GODOT := /Applications/Godot_mono.app/Contents/MacOS/Godot
DEPLOY_GODOT := /opt/the-room/godot-app/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64

SLN     := The Room.sln
PORT      ?= 60010
HOST      ?= 127.0.0.1
N         ?= 2
CHARACTER ?=
CHAR_FLAG := $(if $(CHARACTER),--character=$(CHARACTER),)
MODEL ?=
MODEL_FLAG := $(if $(MODEL),--model=$(MODEL),)
CLOSEUP ?=
CLOSEUP_FLAG := $(if $(CLOSEUP),--closeup,)
PROP ?=
PROP_FLAG := $(if $(PROP),--prop=$(PROP),)

# VPS deploy target (see plan/phase-1-network-spike.md). SSH host is an alias from ~/.ssh/config;
# app runs isolated under its own system user/service, never as part of DEPLOY_HOST's other apps.
DEPLOY_HOST ?= kios-chat
DEPLOY_PATH ?= /opt/the-room/app
DEPLOY_USER ?= theroom
DEPLOY_SERVICE ?= the-room-lobby.service

.PHONY: help \
	install setup \
	build run-server run-client run-local run-bots run-lobby run-client-menu preview-animations \
	clean \
	test lobby-test \
	deploy-server deploy-nginx deploy-logs deploy-status \
	export-server export-client export-mac

## ------------------------------------------------------------------------
## help
## ------------------------------------------------------------------------

help:
	@echo "$(BLUE)The Room — Godot 4.7 (C#) knife-fight arena$(RESET)"
	@echo ""
	@echo "$(BLUE)Setup$(RESET)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "install" "Restore NuGet packages ($(YELLOW)dotnet restore$(RESET))"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "setup" "install + sanity build, prints next steps"
	@echo ""
	@echo "$(BLUE)App$(RESET)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "build" "Build the C# project ($(YELLOW)dotnet build$(RESET))"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "run-server" "Run a dedicated headless server ($(YELLOW)godot --headless --server$(RESET)). PORT=$(PORT)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "run-client" "Run one windowed client and connect ($(YELLOW)godot --connect$(RESET)). HOST=$(HOST) PORT=$(PORT) CHARACTER=$(CHARACTER)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "run-local" "Spawn 1 local server + N windowed clients. N=$(N) PORT=$(PORT) CHARACTER=$(CHARACTER)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "run-bots" "Connect N headless wander/stab/ability bots to a server (see run-server). N=$(N) HOST=$(HOST) PORT=$(PORT) CHARACTER=$(CHARACTER)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "run-lobby" "Run the lobby locally; it starts rooms from this checkout ($(YELLOW)dotnet run services/lobby$(RESET)). Then: make run-client-menu"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "run-client-menu" "Windowed client on the main menu, using the local lobby (--api=http://127.0.0.1:5310)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "preview-animations" "Side-view preview of a model playing every shared clip ($(YELLOW)tools/AnimationPreview.tscn$(RESET)). MODEL=res://...fbx (default Zain), CLOSEUP=1 for hands/knife, PROP=res://...tscn for another held prop"
	@echo ""
	@echo "$(BLUE)Quality$(RESET)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "clean" "Remove build/editor caches ($(YELLOW).godot/mono, bin, obj$(RESET))"
	@echo ""
	@echo "$(BLUE)Test$(RESET)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "test" "Run both suites: game ($(YELLOW)GoDotTest$(RESET), tests/) + lobby ($(YELLOW)xUnit$(RESET), services/Lobby.Tests)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "lobby-test" "Run only the lobby's xUnit tests"
	@echo ""
	@echo "$(BLUE)Deploy$(RESET)"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "deploy-server" "rsync + build game + publish lobby + restart on $(DEPLOY_HOST) ($(YELLOW)systemd: $(DEPLOY_SERVICE)$(RESET))"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "deploy-nginx" "Install deploy/nginx/room-api.iscoded.com.conf on $(DEPLOY_HOST) and reload nginx"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "deploy-status" "Show the deployed server's systemd status"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "deploy-logs" "Tail the deployed server's journal"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "export-server" "Export a standalone Linux server binary to build/server/"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "export-client" "Export a standalone Windows client .exe to build/client/"
	@printf "  $(GREEN)%-14s$(RESET) %s\n" "export-mac" "Export a macOS app (universal) to build/macos/The Room.app"

## ------------------------------------------------------------------------
## Setup
## ------------------------------------------------------------------------

install:
	@cd "$(ROOT)" && "$(DOTNET)" restore "$(SLN)"

setup: install build
	@echo ""
	@echo "$(GREEN)Setup done.$(RESET) Next steps:"
	@echo "  - Local playtest:   make run-local N=3"
	@echo "  - Just the server:  make run-server PORT=$(PORT)"
	@echo "  - Just a client:    make run-client HOST=127.0.0.1 PORT=$(PORT)"

## ------------------------------------------------------------------------
## App
## ------------------------------------------------------------------------

build:
	@cd "$(ROOT)" && "$(DOTNET)" build "$(SLN)"

run-server: build
	@cd "$(ROOT)" && "$(GODOT)" --headless --path . -- --server --port=$(PORT)

run-client: build
	@cd "$(ROOT)" && "$(GODOT)" --path . -- --connect=$(HOST) --port=$(PORT) $(CHAR_FLAG)

run-local: build
	@cd "$(ROOT)" && \
	echo "$(GREEN)Starting server on port $(PORT)...$(RESET)" && \
	"$(GODOT)" --headless --path . -- --server --port=$(PORT) & \
	SERVER_PID=$$!; \
	trap 'echo "Stopping server ($(YELLOW)pid $$SERVER_PID$(RESET))..."; kill $$SERVER_PID 2>/dev/null || true' EXIT; \
	sleep 2; \
	for i in $$(seq 1 $(N)); do \
		echo "$(GREEN)Starting client $$i...$(RESET)"; \
		"$(GODOT)" --path . -- --connect=127.0.0.1 --port=$(PORT) --name=Player$$i $(CHAR_FLAG) & \
		sleep 0.5; \
	done; \
	wait

run-bots: build
	@cd "$(ROOT)" && \
	for i in $$(seq 1 $(N)); do \
		echo "$(GREEN)Starting bot $$i...$(RESET)"; \
		"$(GODOT)" --headless --path . -- --connect=$(HOST) --port=$(PORT) --name=Bot$$i --bot $(CHAR_FLAG) & \
		sleep 0.3; \
	done; \
	wait

preview-animations: build
	@cd "$(ROOT)" && "$(GODOT)" --path . res://tools/AnimationPreview.tscn -- $(MODEL_FLAG) $(CLOSEUP_FLAG) $(PROP_FLAG)

# Local lobby: rooms run from this checkout. Ports 60410+ so it can't collide with run-server.
LOBBY_URL := http://127.0.0.1:5310
run-lobby: build
	@cd "$(ROOT)" && mkdir -p .lobby && \
	LOBBY_LISTEN=$(LOBBY_URL) GODOT_BIN="$(GODOT)" GAME_PATH="$(ROOT)" LOBBY_DB="$(ROOT)/.lobby/lobby.db" \
	PUBLIC_HOST=127.0.0.1 MAIN_ROOM_PORT=60410 ROOM_PORT_MIN=60411 ROOM_PORT_MAX=60420 \
	"$(DOTNET)" run --project services/lobby/Lobby.csproj

run-client-menu: build
	@cd "$(ROOT)" && "$(GODOT)" --path . -- --api=$(LOBBY_URL)

## ------------------------------------------------------------------------
## Quality
## ------------------------------------------------------------------------

clean:
	@cd "$(ROOT)" && rm -rf .godot/mono bin obj
	@echo "$(GREEN)Cleaned .godot/mono, bin/, obj/.$(RESET)"

## ------------------------------------------------------------------------
## Test
## ------------------------------------------------------------------------

test: build lobby-test
	@cd "$(ROOT)" && "$(GODOT)" --headless --path . tests/TestRunner.tscn -- --run-tests --quit-on-finish

# RollForward: the lobby targets net8.0 (the VPS runtime); this lets it run on a newer local one.
lobby-test:
	@cd "$(ROOT)" && DOTNET_ROLL_FORWARD=Major "$(DOTNET)" test services/Lobby.Tests/Lobby.Tests.csproj

## ------------------------------------------------------------------------
## Deploy
## ------------------------------------------------------------------------

deploy-server:
	@echo "$(GREEN)Syncing to $(DEPLOY_HOST):$(DEPLOY_PATH)...$(RESET)"
	@rsync -az --delete \
		--exclude '.git' --exclude '.godot' --exclude 'bin' --exclude 'obj' --exclude '/build' \
		--exclude '.lobby' --exclude '*.db' --exclude '*.db-*' --exclude 'season_stats.json' \
		"$(ROOT)/" "$(DEPLOY_HOST):$(DEPLOY_PATH)/"
	@echo "$(GREEN)Building game + publishing lobby on $(DEPLOY_HOST)...$(RESET)"
	@ssh "$(DEPLOY_HOST)" '\
		mkdir -p /opt/the-room/data /opt/the-room/lobby && \
		chown -R $(DEPLOY_USER):$(DEPLOY_USER) "$(DEPLOY_PATH)" /opt/the-room/data /opt/the-room/lobby && \
		sudo -u $(DEPLOY_USER) bash -c "export PATH=/opt/the-room/dotnet:\$$PATH DOTNET_ROOT=/opt/the-room/dotnet; cd $(DEPLOY_PATH) && \
			dotnet build \"The Room.sln\" && \
			$(DEPLOY_GODOT) --headless --path . --import >/dev/null 2>&1 && \
			dotnet publish services/lobby/Lobby.csproj -c Release -o /opt/the-room/lobby"'
	@echo "$(GREEN)Installing $(DEPLOY_SERVICE)...$(RESET)"
	@ssh "$(DEPLOY_HOST)" '\
		install -m 644 "$(DEPLOY_PATH)/deploy/systemd/$(DEPLOY_SERVICE)" /etc/systemd/system/$(DEPLOY_SERVICE) && \
		systemctl daemon-reload && \
		if systemctl list-unit-files the-room-server.service --no-legend | grep -q the-room-server; then \
			echo "Retiring the-room-server.service (the lobby runs the main room now)"; \
			systemctl disable --now the-room-server.service && rm -f /etc/systemd/system/the-room-server.service && systemctl daemon-reload; \
		fi && \
		systemctl enable $(DEPLOY_SERVICE) >/dev/null 2>&1 && systemctl restart $(DEPLOY_SERVICE) && \
		for i in $$(seq 1 20); do curl -fs http://127.0.0.1:5310/api/health >/dev/null && break; sleep 1; done && \
		systemctl is-active $(DEPLOY_SERVICE) && curl -fs http://127.0.0.1:5310/api/health && echo'
	@echo "$(GREEN)Deployed.$(RESET) make deploy-status / deploy-logs to check on it."

deploy-nginx:
	@ssh "$(DEPLOY_HOST)" '\
		live=/etc/nginx/sites-available/room-api.iscoded.com.conf; new=$$(mktemp); backup=$$(mktemp) && \
		cat > "$$new" && cp "$$live" "$$backup" && install -m 644 "$$new" "$$live" && \
		if nginx -t; then systemctl reload nginx && echo "nginx reloaded"; \
		else echo "nginx -t failed; restoring previous config"; cp "$$backup" "$$live"; exit 1; fi; \
		rm -f "$$new" "$$backup"' < "$(ROOT)/deploy/nginx/room-api.iscoded.com.conf"

deploy-status:
	@ssh "$(DEPLOY_HOST)" "systemctl status $(DEPLOY_SERVICE) --no-pager -l"

deploy-logs:
	@ssh "$(DEPLOY_HOST)" "journalctl -u $(DEPLOY_SERVICE) -n 100 --no-pager"

export-server: build
	@mkdir -p "$(ROOT)/build/server"
	@cd "$(ROOT)" && "$(GODOT)" --headless --path . --export-release server build/server/the-room-server.x86_64
	@echo "$(GREEN)Exported to build/server/the-room-server.x86_64$(RESET) (Linux x86_64 — needs Godot's Linux export templates installed for this platform's Godot editor)"

export-mac: build
	@mkdir -p "$(ROOT)/build/macos"
	@cd "$(ROOT)" && "$(GODOT)" --headless --path . --export-release mac "build/macos/The Room.app"
	@echo "$(GREEN)Exported to build/macos/The Room.app$(RESET) (universal: Apple Silicon + Intel; ad-hoc signed)"

export-client: build
	@mkdir -p "$(ROOT)/build/client"
	@cd "$(ROOT)" && "$(GODOT)" --headless --path . --export-release client build/client/the-room.exe
	@echo "$(GREEN)Exported to build/client/the-room.exe$(RESET) (Windows x86_64 — needs Godot's Windows export templates installed for this platform's Godot editor)"
