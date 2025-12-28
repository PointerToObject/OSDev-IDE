#ifndef PREPROCESSOR_H
#define PREPROCESSOR_H

#include "../../Includes.h"

// Preprocesses source code, handling #include and #define
// Returns newly allocated string with preprocessed content
// base_dir: directory to search for include files (use "." for current dir)
char* preprocess(const char* source, const char* base_dir);

#endif // PREPROCESSOR_H