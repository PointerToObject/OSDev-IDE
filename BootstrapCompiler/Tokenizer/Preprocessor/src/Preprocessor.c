#include "../Preprocessor.h"
#include <stdio.h>
#include <ctype.h>

#define MAX_DEFINES 256
#define MAX_INCLUDE_DEPTH 32

typedef struct {
    char* name;
    char* value;
} Define;

typedef struct {
    Define defines[MAX_DEFINES];
    int define_count;
    int include_depth;
    char* base_dir;
} PreprocessorState;

static PreprocessorState* pp_state_create(const char* base_dir) {
    PreprocessorState* state = malloc(sizeof(PreprocessorState));
    state->define_count = 0;
    state->include_depth = 0;
    state->base_dir = base_dir ? _strdup(base_dir) : _strdup(".");
    return state;
}

static void pp_state_free(PreprocessorState* state) {
    for (int i = 0; i < state->define_count; i++) {
        free(state->defines[i].name);
        free(state->defines[i].value);
    }
    free(state->base_dir);
    free(state);
}

static void add_define(PreprocessorState* state, const char* name, const char* value) {
    if (state->define_count >= MAX_DEFINES) {
        fprintf(stderr, "Too many #defines\n");
        return;
    }

    // Check if already defined, if so update it
    for (int i = 0; i < state->define_count; i++) {
        if (strcmp(state->defines[i].name, name) == 0) {
            free(state->defines[i].value);
            state->defines[i].value = value ? _strdup(value) : _strdup("");
            return;
        }
    }

    state->defines[state->define_count].name = _strdup(name);
    state->defines[state->define_count].value = value ? _strdup(value) : _strdup("");
    state->define_count++;
}

static const char* get_define(PreprocessorState* state, const char* name) {
    for (int i = 0; i < state->define_count; i++) {
        if (strcmp(state->defines[i].name, name) == 0) {
            return state->defines[i].value;
        }
    }
    return NULL;
}

static char* read_file(const char* filename) {
    FILE* f = fopen(filename, "rb");
    if (!f) {
        return NULL;
    }

    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);

    char* content = malloc(size + 1);
    fread(content, 1, size, f);
    content[size] = '\0';
    fclose(f);

    return content;
}

static char* preprocess_internal(PreprocessorState* state, const char* source);

static void skip_whitespace_inline(const char** p) {
    while (**p == ' ' || **p == '\t') (*p)++;
}

static void extract_word(const char** p, char* buf, int max_len) {
    int i = 0;
    while ((isalnum(**p) || **p == '_' || **p == '.' || **p == '/' || **p == '\\') && i < max_len - 1) {
        buf[i++] = **p;
        (*p)++;
    }
    buf[i] = '\0';
}

static int is_identifier_start(char c) {
    return isalpha(c) || c == '_';
}

static int is_identifier_char(char c) {
    return isalnum(c) || c == '_';
}

static char* preprocess_internal(PreprocessorState* state, const char* source) {
    if (state->include_depth >= MAX_INCLUDE_DEPTH) {
        fprintf(stderr, "Include depth too deep\n");
        return _strdup("");
    }
    state->include_depth++;

    size_t capacity = strlen(source) * 2 + 4096;
    char* result = malloc(capacity);
    size_t result_len = 0;

    const char* p = source;

    while (*p) {
        // Handle preprocessor directives
        if (*p == '#') {
            p++;
            skip_whitespace_inline(&p);

            char directive[64];
            extract_word(&p, directive, sizeof(directive));
            skip_whitespace_inline(&p);

            if (strcmp(directive, "include") == 0) {
                char filename[256];
                if (*p == '"' || *p == '<') {
                    char end_char = (*p == '"') ? '"' : '>';
                    p++;

                    int i = 0;
                    while (*p && *p != end_char && *p != '\n' && i < sizeof(filename) - 1) {
                        filename[i++] = *p++;
                    }
                    filename[i] = '\0';

                    if (*p == end_char) p++;

                    char fullpath[512];
                    if (strchr(filename, '/') || strchr(filename, '\\')) {
                        snprintf(fullpath, sizeof(fullpath), "%s", filename);
                    }
                    else {
                        snprintf(fullpath, sizeof(fullpath), "%s/%s", state->base_dir, filename);
                    }

                    char* included = read_file(fullpath);
                    if (included) {
                        char* processed = preprocess_internal(state, included);
                        free(included);

                        size_t proc_len = strlen(processed);
                        while (result_len + proc_len + 2 >= capacity) {
                            capacity *= 2;
                            result = realloc(result, capacity);
                        }

                        memcpy(result + result_len, processed, proc_len);
                        result_len += proc_len;
                        result[result_len++] = '\n';

                        free(processed);
                    }
                }
            }
            else if (strcmp(directive, "define") == 0) {
                char name[128], value[512];
                extract_word(&p, name, sizeof(name));
                skip_whitespace_inline(&p);

                int i = 0;
                while (*p && *p != '\n' && i < sizeof(value) - 1) {
                    value[i++] = *p++;
                }
                value[i] = '\0';

                // Trim trailing whitespace
                while (i > 0 && isspace((unsigned char)value[i - 1])) {
                    value[--i] = '\0';
                }

                add_define(state, name, value);
            }
            else if (strcmp(directive, "pragma") == 0 ||
                strcmp(directive, "ifndef") == 0 ||
                strcmp(directive, "ifdef") == 0 ||
                strcmp(directive, "endif") == 0) {
                // Skip these directives entirely
            }

            // Skip to end of line
            while (*p && *p != '\n') p++;
            if (*p == '\n') p++;
            continue;
        }

        // Handle identifier - check for macro expansion
        if (is_identifier_start(*p)) {
            const char* start = p;
            char ident[128];
            int i = 0;
            while (is_identifier_char(*p) && i < sizeof(ident) - 1) {
                ident[i++] = *p++;
            }
            ident[i] = '\0';

            const char* replacement = get_define(state, ident);
            if (replacement && strlen(replacement) > 0) {
                // Expand the macro
                size_t rep_len = strlen(replacement);
                while (result_len + rep_len + 1 >= capacity) {
                    capacity *= 2;
                    result = realloc(result, capacity);
                }
                memcpy(result + result_len, replacement, rep_len);
                result_len += rep_len;
            }
            else {
                // Keep original identifier
                size_t ident_len = p - start;
                while (result_len + ident_len + 1 >= capacity) {
                    capacity *= 2;
                    result = realloc(result, capacity);
                }
                memcpy(result + result_len, start, ident_len);
                result_len += ident_len;
            }
            continue;
        }

        // Copy character as-is
        if (result_len >= capacity - 1) {
            capacity *= 2;
            result = realloc(result, capacity);
        }
        result[result_len++] = *p++;
    }

    result[result_len] = '\0';
    state->include_depth--;
    return result;
}

char* preprocess(const char* source, const char* base_dir) {
    PreprocessorState* state = pp_state_create(base_dir);
    char* result = preprocess_internal(state, source);
    pp_state_free(state);
    return result;
}