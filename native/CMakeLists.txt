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

option(BUILD_TESTING "Build native integration tests" OFF)
set(MODEL_PATH "" CACHE STRING "Model file for native integration tests, relative to the repository root or absolute")

# -------------------------------------------------------------------
# REQUIRED BUILD-TIME PARAMETERS
# -------------------------------------------------------------------
if (NOT DEFINED LLAMA_INCLUDE_ROOT)
    message(FATAL_ERROR "LLAMA_INCLUDE_ROOT must be set")
endif()

if (NOT DEFINED LLAMA_LIBRARY_ROOT)
    message(FATAL_ERROR "LLAMA_LIBRARY_ROOT must be set")
endif()

if (NOT DEFINED LLAMA_SOURCE_VERSION OR LLAMA_SOURCE_VERSION STREQUAL "")
    message(FATAL_ERROR "LLAMA_SOURCE_VERSION must be set")
endif()


get_filename_component(LLAMA_INCLUDE_ROOT "${LLAMA_INCLUDE_ROOT}" ABSOLUTE)
get_filename_component(LLAMA_LIBRARY_ROOT "${LLAMA_LIBRARY_ROOT}" ABSOLUTE)

message(STATUS "ABS LLAMA_INCLUDE_ROOT = ${LLAMA_INCLUDE_ROOT}")
message(STATUS "ABS LLAMA_LIBRARY_ROOT = ${LLAMA_LIBRARY_ROOT}")

set(LLAMA_LIBRARY "${LLAMA_LIBRARY_ROOT}/${CMAKE_SHARED_LIBRARY_PREFIX}llama${CMAKE_SHARED_LIBRARY_SUFFIX}")
if (NOT EXISTS "${LLAMA_LIBRARY}")
    message(FATAL_ERROR "Core llama library not found: ${LLAMA_LIBRARY}")
endif()

# -------------------------------------------------------------------
# Adapter library
# -------------------------------------------------------------------
add_library(llama_adapter SHARED
    src/llama_adapter_core.cpp
    src/llama_adapter_structured_output.cpp
    src/llama_adapter.cpp
)

target_include_directories(llama_adapter
    PUBLIC
        ${LLAMA_INCLUDE_ROOT}
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(llama_adapter
    PRIVATE
        LLAMA_ADAPTER_SOURCE_VERSION="${LLAMA_SOURCE_VERSION}"
)

# IMPORTANT:
# - Link using FULL PATH
# - No target_link_directories
target_link_libraries(llama_adapter
    PRIVATE
        "${LLAMA_LIBRARY}"
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
# Copy vendor dependencies & licenses
# -------------------------------------------------------------------
if (APPLE)
    set(lib_pattern "lib*.dylib")
else()
    set(lib_pattern "lib*.so*")
endif()
file(GLOB runtime_libraries CONFIGURE_DEPENDS "${LLAMA_LIBRARY_ROOT}/${lib_pattern}")

add_custom_command(TARGET llama_adapter POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy
        "${LLAMA_LIBRARY_ROOT}/LICENSE"
        "$<TARGET_FILE_DIR:llama_adapter>/LICENSE-llama"
    COMMAND ${CMAKE_COMMAND} -E copy
        "${CMAKE_SOURCE_DIR}/../LICENSE"
        "$<TARGET_FILE_DIR:llama_adapter>/LICENSE"
    COMMENT "Copying licenses to build directory"
    VERBATIM
)

add_custom_command(TARGET llama_adapter POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${runtime_libraries} "$<TARGET_FILE_DIR:llama_adapter>"
    VERBATIM
)

# -------------------------------------------------------------------
# Integration test
# -------------------------------------------------------------------
enable_testing()
if (BUILD_TESTING)
    if (MODEL_PATH STREQUAL "")
        message(FATAL_ERROR "MODEL_PATH must be set when BUILD_TESTING=ON")
    endif()
    get_filename_component(test_model_path "${MODEL_PATH}" ABSOLUTE
        BASE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/..")
    if (NOT EXISTS "${test_model_path}" OR IS_DIRECTORY "${test_model_path}")
        message(FATAL_ERROR "MODEL_PATH must name an existing model file: ${test_model_path}")
    endif()

    include(FetchContent)
    set(JSON_BuildTests OFF CACHE INTERNAL "")
    set(JSON_Install OFF CACHE INTERNAL "")
    FetchContent_Declare(nlohmann_json
        GIT_REPOSITORY https://github.com/nlohmann/json.git
        GIT_TAG v3.11.3
    )
    FetchContent_MakeAvailable(nlohmann_json)

    add_executable(integration_test tests/integration_test.cpp)
    target_link_libraries(integration_test
        PRIVATE
            llama_adapter
            nlohmann_json::nlohmann_json
    )
    set_target_properties(integration_test PROPERTIES
        BUILD_RPATH "${rpath_origin}"
    )
    add_test(NAME integration_test COMMAND integration_test "${test_model_path}")
    add_executable(context_test tests/context_test.cpp)
    target_link_libraries(context_test PRIVATE llama_adapter "${LLAMA_LIBRARY}")
    set_target_properties(context_test PROPERTIES BUILD_RPATH "${rpath_origin}")
    add_test(NAME context_test COMMAND context_test "${test_model_path}")
endif()
