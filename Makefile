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
CACHE_DIR := $(VENDOR_DIR)/cache/$(LLAMA_VERSION)
CHECKSUMS_DIR := checksums/llama
CHECKSUMS_FILE := $(CHECKSUMS_DIR)/$(LLAMA_VERSION).sha256
PIN_LLAMA_SCRIPT := scripts/pin_llama.sh

ARTIFACT_NAME := $(PROJECT)-$(LLAMA_VERSION)-bin-$(PLATFORM).tar.gz
ARTIFACT_URL := https://github.com/$(GITHUB_ORG)/$(GITHUB_REPO)/releases/download/$(LLAMA_VERSION)/$(ARTIFACT_NAME)
ARTIFACT_CACHE_PATH := $(CACHE_DIR)/$(ARTIFACT_NAME)
SHA_FILE := $(VENDOR_PATH)/SHA256SUMS
HEADERS_ARCHIVE_NAME := llama-$(LLAMA_VERSION)-headers.zip
HEADERS_ARCHIVE_URL := https://github.com/$(GITHUB_ORG)/$(GITHUB_REPO)/archive/$(LLAMA_VERSION).zip
HEADERS_ARCHIVE_PATH := $(CACHE_DIR)/$(HEADERS_ARCHIVE_NAME)

MODEL_PATH ?= models/llama.bin

# ------------------------------------------------------------
# Tools
# ------------------------------------------------------------
CURL := curl -fL
SHA256SUM := shasum -a 256
MKDIR := mkdir -p
TAR := tar
RM := rm -rf

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
BENCH_GRPCURL ?= http://localhost:5000

# ------------------------------------------------------------
# gRPC runtime config
# ------------------------------------------------------------
LLAMA_RUNTIME_GRPC_PROJECT := src/LlamaRuntime.Presentation.Grpc
PACKAGE_DIR := dist
PUBLISH_SINGLE_FILE ?= true
ENABLE_COMPRESSION_IN_SINGLE_FILE ?= true
PUBLISH_READY_TO_RUN ?= false

# ------------------------------------------------------------
# Phony targets
# ------------------------------------------------------------
.PHONY: \
	all init verify pin-llama clean native-build \
	vendor/include vendor/binary \
	native-integration-tests \
	bench-llama-runtime-grpc bench-llama-rest \
	run-llama-rest-server \
	llama-runtime-grpc-build run-llama-runtime-grpc

# ------------------------------------------------------------
# Vendor headers
# ------------------------------------------------------------
vendor/include:
	@set -euo pipefail; \
		if [ ! -f "$(CHECKSUMS_FILE)" ]; then \
			echo ">>> Missing checksum manifest $(CHECKSUMS_FILE)"; \
			echo ">>> Add the pinned manifest for LLAMA_VERSION=$(LLAMA_VERSION) or run 'make pin-llama LLAMA_VERSION=bNNNN' when intentionally onboarding a new upstream release."; \
			exit 1; \
		fi; \
	manifest_entry="$$(awk '$$2 == "$(HEADERS_ARCHIVE_NAME)" { print; found=1 } END { if (!found) exit 1 }' "$(CHECKSUMS_FILE)")" || { \
		echo ">>> Missing checksum entry for $(HEADERS_ARCHIVE_NAME) in $(CHECKSUMS_FILE)"; \
		exit 1; \
	}; \
	$(MKDIR) "$(CACHE_DIR)" "$(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)"; \
	if [ ! -f "$(HEADERS_ARCHIVE_PATH)" ]; then \
		echo ">>> Downloading headers LLAMA_VERSION=$(LLAMA_VERSION)"; \
		$(CURL) "$(HEADERS_ARCHIVE_URL)" -o "$(HEADERS_ARCHIVE_PATH)"; \
	else \
		echo ">>> Using cached headers archive $(HEADERS_ARCHIVE_NAME)"; \
	fi; \
	printf '%s\n' "$$manifest_entry" | (cd "$(CACHE_DIR)" && $(SHA256SUM) -c -); \
	echo ">>> Extracting verified headers LLAMA_VERSION=$(LLAMA_VERSION)"; \
	rm -rf "$(INCLUDE_PATH)" "$(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(GITHUB_REPO)-$(LLAMA_VERSION)"; \
	$(MKDIR) "$(INCLUDE_PATH)"; \
	unzip -q -o "$(HEADERS_ARCHIVE_PATH)" -d "$(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)"; \
	mv "$(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(GITHUB_REPO)-$(LLAMA_VERSION)/include/"* "$(INCLUDE_PATH)/"; \
	mv "$(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(GITHUB_REPO)-$(LLAMA_VERSION)/ggml/include/"* "$(INCLUDE_PATH)/" || true; \
	rm -rf "$(VENDOR_DIR)/$(PROJECT)/$(LLAMA_VERSION)/$(GITHUB_REPO)-$(LLAMA_VERSION)"

# ------------------------------------------------------------
# Vendor binaries
# ------------------------------------------------------------
vendor/binary:
	@set -euo pipefail; \
		if [ ! -f "$(CHECKSUMS_FILE)" ]; then \
			echo ">>> Missing checksum manifest $(CHECKSUMS_FILE)"; \
			echo ">>> Add the pinned manifest for LLAMA_VERSION=$(LLAMA_VERSION) or run 'make pin-llama LLAMA_VERSION=bNNNN' when intentionally onboarding a new upstream release."; \
			exit 1; \
		fi; \
	manifest_entry="$$(awk '$$2 == "$(ARTIFACT_NAME)" { print; found=1 } END { if (!found) exit 1 }' "$(CHECKSUMS_FILE)")" || { \
		echo ">>> Missing checksum entry for $(ARTIFACT_NAME) in $(CHECKSUMS_FILE)"; \
		exit 1; \
	}; \
	$(MKDIR) "$(CACHE_DIR)"; \
	if [ ! -f "$(ARTIFACT_CACHE_PATH)" ]; then \
		echo ">>> Downloading binary LLAMA_VERSION=$(LLAMA_VERSION) PLATFORM=$(PLATFORM)"; \
		$(CURL) -o "$(ARTIFACT_CACHE_PATH)" "$(ARTIFACT_URL)"; \
	else \
		echo ">>> Using cached binary archive $(ARTIFACT_NAME)"; \
	fi; \
	printf '%s\n' "$$manifest_entry" | (cd "$(CACHE_DIR)" && $(SHA256SUM) -c -); \
	echo ">>> Extracting verified binary LLAMA_VERSION=$(LLAMA_VERSION) PLATFORM=$(PLATFORM)"; \
	rm -rf "$(VENDOR_PATH)"; \
	$(MKDIR) "$(VENDOR_PATH)"; \
	$(TAR) -xf "$(ARTIFACT_CACHE_PATH)" -C "$(VENDOR_PATH)"; \
	cd "$(VENDOR_PATH)" && \
		find . -type f ! -name 'SHA256SUMS' ! -name 'meta.json' \
			-exec $(SHA256SUM) {} \; > SHA256SUMS

# ------------------------------------------------------------
# Init
# ------------------------------------------------------------
init: vendor/include vendor/binary

# ------------------------------------------------------------
# Verify
# ------------------------------------------------------------
verify:
	@set -euo pipefail; \
		if [ ! -f "$(CHECKSUMS_FILE)" ]; then \
			echo ">>> Missing checksum manifest $(CHECKSUMS_FILE)"; \
			echo ">>> Add the pinned manifest for LLAMA_VERSION=$(LLAMA_VERSION) or run 'make pin-llama LLAMA_VERSION=bNNNN' when intentionally onboarding a new upstream release."; \
			exit 1; \
		fi; \
	if [ ! -f "$(HEADERS_ARCHIVE_PATH)" ]; then \
		echo ">>> Missing cached headers archive $(HEADERS_ARCHIVE_PATH). Run make init first."; \
		exit 1; \
	fi; \
	manifest_entry="$$(awk '$$2 == "$(HEADERS_ARCHIVE_NAME)" { print; found=1 } END { if (!found) exit 1 }' "$(CHECKSUMS_FILE)")" || { \
		echo ">>> Missing checksum entry for $(HEADERS_ARCHIVE_NAME) in $(CHECKSUMS_FILE)"; \
		exit 1; \
	}; \
	printf '%s\n' "$$manifest_entry" | (cd "$(CACHE_DIR)" && $(SHA256SUM) -c -); \
	if [ ! -f "$(ARTIFACT_CACHE_PATH)" ]; then \
		echo ">>> Missing cached binary archive $(ARTIFACT_CACHE_PATH). Run make init first."; \
		exit 1; \
	fi; \
	manifest_entry="$$(awk '$$2 == "$(ARTIFACT_NAME)" { print; found=1 } END { if (!found) exit 1 }' "$(CHECKSUMS_FILE)")" || { \
		echo ">>> Missing checksum entry for $(ARTIFACT_NAME) in $(CHECKSUMS_FILE)"; \
		exit 1; \
	}; \
	printf '%s\n' "$$manifest_entry" | (cd "$(CACHE_DIR)" && $(SHA256SUM) -c -); \
	if [ -f "$(SHA_FILE)" ]; then \
		cd "$(VENDOR_PATH)" && $(SHA256SUM) -c SHA256SUMS; \
	fi

# ------------------------------------------------------------
# llama.cpp version bump
# ------------------------------------------------------------
pin-llama:
	@if [ "$(origin LLAMA_VERSION)" != "command line" ] || [ -z "$(LLAMA_VERSION)" ] || [ "$(LLAMA_VERSION)" = "unknown" ]; then \
		echo ">>> Usage: make pin-llama LLAMA_VERSION=bNNNN"; \
		exit 1; \
	fi
	@if [ ! -f "$(PIN_LLAMA_SCRIPT)" ]; then \
		echo ">>> Missing pin helper $(PIN_LLAMA_SCRIPT)"; \
		exit 1; \
	fi
	@bash "$(PIN_LLAMA_SCRIPT)" "$(CURDIR)" "$(LLAMA_VERSION)" "$(CHECKSUMS_DIR)" "$(GITHUB_ORG)" "$(GITHUB_REPO)"

# ------------------------------------------------------------
# Native build
# ------------------------------------------------------------
native-build:
	$(MKDIR) $(CMAKE_BUILD_DIR)
	cd $(CMAKE_BUILD_DIR) && cmake .. \
		-DLLAMA_INCLUDE_ROOT="../vendor/llama/$(LLAMA_VERSION)/include" \
		-DLLAMA_LIBRARY_ROOT="../vendor/llama/$(LLAMA_VERSION)/$(PLATFORM)/llama-$(LLAMA_VERSION)" \
		-DLLAMA_SOURCE_VERSION="$(LLAMA_VERSION)" \
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
	@echo ">>> Cleaning package dir..."
	rm -rf $(PACKAGE_DIR)/*
	dotnet publish $(LLAMA_RUNTIME_GRPC_PROJECT) \
		-c Release \
		-r $(DOTNET_RUNTIME) \
		--self-contained true \
		/p:PublishSingleFile=$(PUBLISH_SINGLE_FILE) \
		/p:PublishReadyToRun=$(PUBLISH_READY_TO_RUN) \
		/p:EnableCompressionInSingleFile=$(ENABLE_COMPRESSION_IN_SINGLE_FILE) \
		/p:DebugType=None \
		/p:DebugSymbols=false \
		-o $(PACKAGE_DIR)

pack: native-build llama-runtime-grpc-build
	@echo ">>> Copying native adapter and dependencies..."
	cp -R native/build/lib* $(PACKAGE_DIR)/
	@echo ">>> Copying licenses..."
	cp native/build/LICENSE* $(PACKAGE_DIR)/
	@echo ">>> Package created in $(PACKAGE_DIR)"

llama-runtime-grpc-run: pack
	cd $(PACKAGE_DIR) && \
	ASPNETCORE_ENVIRONMENT=Development \
	./LlamaRuntime.Presentation.Grpc

# ------------------------------------------------------------
# Benchmarks
# ------------------------------------------------------------
bench-llama-runtime-grpc:
	BENCH_MODE=LLAMARUNTIMEGRPC \
	BENCH_GRPCURL=$(BENCH_GRPCURL) \
	BENCH_ITERATIONS=$(BENCH_ITERATIONS) \
	BENCH_CONCURRENCY=$(BENCH_CONCURRENCY) \
	BENCH_PROMPT=$(BENCH_PROMPT) \
	BENCH_APIKEY=$(BENCH_APIKEY) \
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
	$(RM) $(VENDOR_DIR) $(CMAKE_BUILD_DIR) $(PACKAGE_DIR)
