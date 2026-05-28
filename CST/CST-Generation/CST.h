#pragma once
#ifndef CST_GEN_H
#define CST_GEN_H

#include "../Parser/Parser.h"

/*
 * CST Code Generator - C to IEC 61131-3 Structured Text
 *
 * Outputs separate files matching TwinCAT 3 project structure:
 *   DUT.st    - TYPE definitions (structs, enums, typedefs)
 *   GVL.st    - Global variable lists (VAR_GLOBAL)
 *   MAIN.st   - PROGRAM MAIN entry point
 *   {name}.st - Individual FUNCTION and FUNCTION_BLOCK POUs
 *
 * output_dir: directory to write all .st files into.
 */

void cst_generate(AST* root, const char* output_dir);

#endif // !CST_GEN_H