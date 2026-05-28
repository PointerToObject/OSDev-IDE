#ifndef PREPROCESSOR_H
#define PREPROCESSOR_H

#include "../../Includes.h"

// Preprocesses source code, handling #include and #define.
// Returns newly allocated string with preprocessed content.
//
// base_dir:        directory to search for include files (use "." for current dir)
// out_target_name: optional. If non-NULL, set to a static string ("beckhoff" /
//                  "allen_bradley") when the source contains a target selector
//                  include like `#include <beckhoff>`. Caller must NOT free.
//                  Set to NULL if no selector was seen.
char* preprocess(const char* source, const char* base_dir, const char** out_target_name);

#endif // PREPROCESSOR_H