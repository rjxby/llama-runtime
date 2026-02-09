cmake_minimum_required(VERSION 3.16)

project(llama_adapter
    VERSION 0.1.0
    LANGUAGES C CXX
)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# -------------------------------------------------------------------
# REQUIRED PARAMETERS
# -------------------------------------------------------------------
if (NOT DEFINED LLAMA_INCLUDE_ROOT)
    message(FATAL_ERROR "LLAMA_INCLUDE_ROOT must be set (vendor/llama/<version>/include)")
endif()

if (NOT DEFINED LLAMA_LIBRARY_ROOT)
    message(FATAL_ERROR "LLAMA_LIBRARY_ROOT must be set (vendor/llama/<version>/<platform>/llama-<version>)")
endif()

message(STATUS "LLAMA_INCLUDE_ROOT = ${LLAMA_INCLUDE_ROOT}")
message(STATUS "LLAMA_LIBRARY_ROOT = ${LLAMA_LIBRARY_ROOT}")

# -------------------------------------------------------------------
# Adapter Library
# -------------------------------------------------------------------
add_library(llama_adapter SHARED
    src/llama_adapter_core.cpp
    src/llama_adapter.cpp
)

# Make headers PUBLIC so dependent targets (tests) can see them
target_include_directories(llama_adapter
    PUBLIC
        ${LLAMA_INCLUDE_ROOT}
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

# Link against llama vendor library
target_link_directories(llama_adapter
    PRIVATE
        ${LLAMA_LIBRARY_ROOT}
)

target_link_libraries(llama_adapter
    PRIVATE
        llama
)

# Platform-specific output settings
if (APPLE)
    set_target_properties(llama_adapter PROPERTIES
        OUTPUT_NAME "llama_adapter"
        INSTALL_NAME_DIR "@rpath"
    )
else()
    set_target_properties(llama_adapter PROPERTIES
        OUTPUT_NAME "llama_adapter"
    )
endif()

set_target_properties(llama_adapter PROPERTIES
    BUILD_RPATH "${LLAMA_LIBRARY_ROOT}"
    INSTALL_RPATH "${LLAMA_LIBRARY_ROOT}"
)

# Ensure build, runtime, and archive outputs go to CMake binary dir
set_target_properties(llama_adapter PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
)

# -------------------------------------------------------------------
# Integration Test Target
# -------------------------------------------------------------------
# NOTE:
# - This expects tests/integration_test.c to exist at repo root (one level up from native/)
# - If your test is in a different place, update the source path below.
add_executable(integration_test
    ../tests/integration_test.cpp
)

# Link against adapter (inherits include dirs & rpath automatically)
target_link_libraries(integration_test
    PRIVATE
        llama_adapter
)

# Ensure the runtime loader finds the adapter library when running the test from the build dir
set_target_properties(integration_test PROPERTIES
    BUILD_RPATH "${LLAMA_LIBRARY_ROOT};${CMAKE_BINARY_DIR}"
)

# -------------------------------------------------------------------
# enable ctest integration
# -------------------------------------------------------------------
enable_testing()
add_test(NAME integration_test COMMAND integration_test)
