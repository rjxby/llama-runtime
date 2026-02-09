SHELL := /bin/bash

# ------------------------------------------------------------
# Target selection (explicit, never guessed)
# ------------------------------------------------------------
TARGET ?= host

ifeq ($(TARGET),host)
UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Linux)
ENV_FILE := .env.linux
else ifeq ($(UNAME_S),Darwin)
ENV_FILE := .env.macos
else
$(error Unsupported host OS)
endif
else
ENV_FILE := .env.$(TARGET)
endif

ifneq (,$(wildcard .env))
include .env
export $(shell sed 's/=.*//' .env)
else ifneq (,$(wildcard $(ENV_FILE)))
include $(ENV_FILE)
export $(shell sed 's/=.*//' $(ENV_FILE))
else
$(error Missing $(ENV_FILE) and no .env found)
endif

# ------------------------------------------------------------
# Required variables (fail fast)
# ------------------------------------------------------------
PLATFORM ?= unknown
LLAMA_VERSION ?= unknown

ifeq ($(PLATFORM),unknown)
$(error PLATFORM must be set via $(ENV_FILE))
endif

ifeq ($(LLAMA_VERSION),unknown)
$(error LLAMA_VERSION must be set via $(ENV_FILE))
endif

# ------------------------------------------------------------
# Project config
# ------------------------------------------------------------
PROJECT := llama
GITHUB_ORG := ggml-org
GITHUB_REPO := llama.cpp

VENDOR_DIR := vendor
VENDOR_PATH := $(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(PLATFORM)
INCLUDE_PATH := $(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/include

ARTIFACT_NAME := $(PROJECT)-$(LLAMA_VERSION)-bin-$(PLATFORM).tar.gz
ARTIFACT_URL := https://github.com/$(GITHUB_ORG)/$(GITHUB_REPO)/releases/download/$(LLAMA_VERSION)/$(ARTIFACT_NAME)

META_JSON := $(VENDOR_PATH)/meta.json
SHA_FILE := $(VENDOR_PATH)/SHA256SUMS

MODEL_PATH ?= models/llama.bin

# ------------------------------------------------------------
# Tools
# ------------------------------------------------------------
CURL := curl -fL
SHA256SUM := shasum -a 256
MKDIR := mkdir -p
TAR := tar
RM := rm -rf
DATE_UTC := $(shell date -u +%Y-%m-%dT%H:%M:%SZ)

# ------------------------------------------------------------
# Native build
# ------------------------------------------------------------
NATIVE_DIR := native
CMAKE_BUILD_DIR := $(NATIVE_DIR)/build

# ------------------------------------------------------------
# Benchmarks
# ------------------------------------------------------------
BENCH_ITERATIONS ?= 100
BENCH_CONCURRENCY ?= 5
BENCH_PROMPT ?= "Write a short story about a llama learning distributed systems."

# ------------------------------------------------------------
# gRPC runtime config
# ------------------------------------------------------------
LLAMA_RUNTIME_GRPC_PROJECT := src/LlamaRuntime.Presentation.Grpc
LLAMA_RUNTIME_GRPC_BIN := llama-runtime-grpc
LLAMA_RUNTIME_GRPC_OUT := build/$(LLAMA_RUNTIME_GRPC_BIN)

# ------------------------------------------------------------
# Phony targets
# ------------------------------------------------------------
.PHONY: \
	all init verify clean native-build \
	vendor/include vendor/binary \
	native-integration-tests \
	bench-llama-runtime-grpc bench-llama-rest \
	run-llama-rest-server \
	llama-runtime-grpc-build run-llama-runtime-grpc

# ------------------------------------------------------------
# Vendor headers
# ------------------------------------------------------------
vendor/include:
	@if [ -d "$(INCLUDE_PATH)" ] && [ "$$(ls -A $(INCLUDE_PATH))" ]; then \
		echo ">>> Headers already exist — skipping"; \
	else \
		echo ">>> Downloading headers LLAMA_VERSION=$(LLAMA_VERSION)"; \
		$(MKDIR) $(INCLUDE_PATH); \
		$(CURL) https://github.com/$(GITHUB_ORG)/$(GITHUB_REPO)/archive/$(LLAMA_VERSION).zip -o /tmp/$(LLAMA_VERSION).zip; \
		unzip -q /tmp/$(LLAMA_VERSION).zip -d $(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION); \
		mv $(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(GITHUB_REPO)-$(LLAMA_VERSION)/include/* $(INCLUDE_PATH)/; \
		mv $(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(GITHUB_REPO)-$(LLAMA_VERSION)/ggml/include/* $(INCLUDE_PATH)/ || true; \
		rm -rf $(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(GITHUB_REPO)-$(LLAMA_VERSION); \
		rm -f /tmp/$(LLAMA_VERSION).zip; \
	fi

# ------------------------------------------------------------
# Vendor binaries
# ------------------------------------------------------------
vendor/binary:
	@if [ -f "$(SHA_FILE)" ]; then \
		echo ">>> Binary already exists — skipping"; \
	else \
		echo ">>> Downloading binary LLAMA_VERSION=$(LLAMA_VERSION) PLATFORM=$(PLATFORM)"; \
		$(MKDIR) $(VENDOR_PATH); \
		$(CURL) -o /tmp/$(ARTIFACT_NAME) $(ARTIFACT_URL); \
		$(TAR) -xf /tmp/$(ARTIFACT_NAME) -C $(VENDOR_PATH); \
		cd $(VENDOR_PATH) && \
			find . -type f ! -name 'SHA256SUMS' ! -name 'meta.json' \
				-exec $(SHA256SUM) {} \; > SHA256SUMS; \
		rm -f /tmp/$(ARTIFACT_NAME); \
	fi

# ------------------------------------------------------------
# Init
# ------------------------------------------------------------
init: vendor/include vendor/binary
	@sed \
		-e 's|@PROJECT@|$(PROJECT)|g' \
		-e 's|@VERSION@|$(LLAMA_VERSION)|g' \
		-e 's|@PLATFORM@|$(PLATFORM)|g' \
		-e 's|@ARTIFACT@|$(ARTIFACT_NAME)|g' \
		-e 's|@SOURCE@|$(ARTIFACT_URL)|g' \
		-e 's|@GENERATED_AT@|$(DATE_UTC)|g' \
		meta.json.tmpl > $(META_JSON)

# ------------------------------------------------------------
# Verify
# ------------------------------------------------------------
verify:
	cd $(VENDOR_PATH) && $(SHA256SUM) -c SHA256SUMS

# ------------------------------------------------------------
# Native build
# ------------------------------------------------------------
native-build:
	$(MKDIR) $(CMAKE_BUILD_DIR)
	cd $(CMAKE_BUILD_DIR) && cmake .. \
		-DLLAMA_INCLUDE_ROOT="../vendor/llama/$(LLAMA_VERSION)/include" \
		-DLLAMA_LIBRARY_ROOT="../vendor/llama/$(LLAMA_VERSION)/$(PLATFORM)/llama-$(LLAMA_VERSION)" \
		-DCMAKE_BUILD_TYPE=Release
	cmake --build $(CMAKE_BUILD_DIR) --config Release

native-integration-tests:
	cd $(CMAKE_BUILD_DIR) && cmake --build . --target integration_test
	cd $(CMAKE_BUILD_DIR) && ./integration_test ../../$(MODEL_PATH)

# ------------------------------------------------------------
# REST server
# ------------------------------------------------------------
run-llama-rest-server:
	$(VENDOR_PATH)/$(PROJECT)-$(LLAMA_VERSION)/llama-server -m $(MODEL_PATH) --port $(LLAMA_REST_PORT)

# ------------------------------------------------------------
# llama-runtime-grpc
# ------------------------------------------------------------
llama-runtime-grpc-build:
	dotnet publish $(LLAMA_RUNTIME_GRPC_PROJECT) \
		-c Release \
		-r $(DOTNET_RUNTIME) \
		--self-contained true \
		/p:PublishSingleFile=true \
		/p:PublishReadyToRun=true \
		/p:EnableCompressionInSingleFile=true \
		/p:DebugType=None \
		/p:DebugSymbols=false \
		-o $(LLAMA_RUNTIME_GRPC_OUT)

run-llama-runtime-grpc: native-build llama-runtime-grpc-build
	cd $(LLAMA_RUNTIME_GRPC_OUT) && \
	ASPNETCORE_ENVIRONMENT=Development \
	Logging__LogLevel__Default=Warning \
	./LlamaRuntime.Presentation.Grpc

# ------------------------------------------------------------
# Benchmarks
# ------------------------------------------------------------
bench-llama-runtime-grpc:
	BENCH_MODE=LLAMARUNTIMEGRPC \
	BENCH_ITERATIONS=$(BENCH_ITERATIONS) \
	BENCH_CONCURRENCY=$(BENCH_CONCURRENCY) \
	BENCH_PROMPT=$(BENCH_PROMPT) \
	BENCH_APIKEY=$(ApiKeys__Keys__0) \
	dotnet run -c Release --project src/LlamaRuntime.Benchmarks

bench-llama-rest:
	BENCH_MODE=LLAMAREST \
	BENCH_LLAMARESTURL=http://localhost:$(LLAMA_REST_PORT)/completion \
	BENCH_ITERATIONS=$(BENCH_ITERATIONS) \
	BENCH_CONCURRENCY=$(BENCH_CONCURRENCY) \
	BENCH_PROMPT=$(BENCH_PROMPT) \
	dotnet run -c Release --project src/LlamaRuntime.Benchmarks

# ------------------------------------------------------------
# Clean
# ------------------------------------------------------------
clean:
	$(RM) $(VENDOR_DIR) $(CMAKE_BUILD_DIR) build

# ------------------------------------------------------------
# Help
# ------------------------------------------------------------
help:
	@echo "Llama Runtime Makefile"
	@echo "----------------------"
	@echo ""
	@echo "Core:"
	@echo "  make init"
	@echo "  make native-build"
	@echo "  make llama-runtime-grpc-build"
	@echo "  make run-llama-runtime-grpc"
	@echo ""
	@echo "Benchmarks:"
	@echo "  make bench-llama-runtime-grpc"
	@echo "  make bench-llama-rest"
