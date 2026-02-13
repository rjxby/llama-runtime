cmake_minimum_required(VERSION 3.16)

project(llama_adapter
    VERSION 0.1.0
    LANGUAGES C CXX
)

# -------------------------------------------------------------------
# Language / build basics
# -------------------------------------------------------------------
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# -------------------------------------------------------------------
# REQUIRED BUILD-TIME PARAMETERS
# -------------------------------------------------------------------
if (NOT DEFINED LLAMA_INCLUDE_ROOT)
    message(FATAL_ERROR "LLAMA_INCLUDE_ROOT must be set")
endif()

if (NOT DEFINED LLAMA_LIBRARY_ROOT)
    message(FATAL_ERROR "LLAMA_LIBRARY_ROOT must be set")
endif()


get_filename_component(LLAMA_INCLUDE_ROOT "${LLAMA_INCLUDE_ROOT}" ABSOLUTE)
get_filename_component(LLAMA_LIBRARY_ROOT "${LLAMA_LIBRARY_ROOT}" ABSOLUTE)

message(STATUS "ABS LLAMA_INCLUDE_ROOT = ${LLAMA_INCLUDE_ROOT}")
message(STATUS "ABS LLAMA_LIBRARY_ROOT = ${LLAMA_LIBRARY_ROOT}")

# -------------------------------------------------------------------
# Adapter library
# -------------------------------------------------------------------
add_library(llama_adapter SHARED
    src/llama_adapter_core.cpp
    src/llama_adapter.cpp
)

target_include_directories(llama_adapter
    PUBLIC
        ${LLAMA_INCLUDE_ROOT}
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

# IMPORTANT:
# - Link using FULL PATH
# - No target_link_directories
target_link_libraries(llama_adapter
    PRIVATE
        "${LLAMA_LIBRARY_ROOT}/libllama.dylib"
)

# -------------------------------------------------------------------
# Portable RPATH settings
# -------------------------------------------------------------------
if (APPLE)
    set(rpath_origin "@loader_path")
else()
    set(rpath_origin "$ORIGIN")
endif()

set_target_properties(llama_adapter PROPERTIES
    OUTPUT_NAME "llama_adapter"
    BUILD_WITH_INSTALL_RPATH TRUE
    INSTALL_RPATH "${rpath_origin}"
    INSTALL_NAME_DIR "@rpath"
)

# -------------------------------------------------------------------
# Output layout (single folder)
# -------------------------------------------------------------------
set_target_properties(llama_adapter PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
)

# -------------------------------------------------------------------
# Integration test
# -------------------------------------------------------------------
add_executable(integration_test
    ../tests/integration_test.cpp
)

target_link_libraries(integration_test
    PRIVATE
        llama_adapter
)

if (APPLE)
    set_target_properties(integration_test PROPERTIES
        BUILD_RPATH "@executable_path;@loader_path"
    )
endif()

# -------------------------------------------------------------------
# CTest
# -------------------------------------------------------------------
enable_testing()
add_test(NAME integration_test COMMAND integration_test)
