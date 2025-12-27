# OSDev-IDE

**Integrated Development Environment for OS Development on Windows**  
A lightweight IDE for building, compiling, and deploying x86 operating systems with a single **Build & Run** workflow. Includes its own built-in compiler for seamless OS development.
<img width="1039" height="621" alt="image" src="https://github.com/user-attachments/assets/54b365c1-93b1-47fe-a0eb-229aa70b586e" />

---

## Overview

OSDev-IDE simplifies the OS development process on Windows by integrating editing, compilation, and execution in one environment.  

Key features:

- One-click Build & Run workflow
- Built-in compiler included (no need for external toolchains)
- Supports C, Assembly, and custom OS pipelines
- Integrated emulator for testing

---

## Getting Started

### Prerequisites

- Windows 10 or 11
- Visual Studio (for building the IDE itself)
- No external compiler is required — OSDev-IDE provides its own

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/PointerToObject/OSDev-IDE.git
   ```
2. Open the solution:
   Open `BootstrapCompiler.sln` in Visual Studio.
3. Build the IDE:
   Select `Release` and click **Build Solution**.
4. Run OSDev-IDE:
   Launch the IDE and open your OS project.

---

## Usage

### Creating a New OS Project

1. Open OSDev-IDE
2. Click **New Project**
3. Choose a template or create a blank workspace
4. Write your kernel/boot code in the editor

### Build and Run

- **Build** – Compiles the OS source using the **built-in compiler**
- **Run** – Launches the OS in the integrated emulator

### Command Reference

```bash
# Example build command (handled internally by the IDE)
build

# Example run command
run
```

---

## Project Structure

```
/OSDev-IDE
  /BootstrapCompiler      - IDE's built-in compiler source
  /IDE                    - IDE source code
  /x64                    - x86 tooling and samples
  /docs                   - Additional documentation
  README.md               - Project readme
```

---

## Features

| Feature                     | Status        |
|-----------------------------|---------------|
| One-click Build & Run       | Supported     |
| Built-in compiler           | Supported     |
| C & Assembly compilation    | Supported     |
| Emulator integration        | Supported     |
| Debug & breakpoint support  | In progress   |

---

## Contributing

Contributions are welcome:

1. Fork the repository
2. Create a feature branch:
   ```bash
   git checkout -b feature/FeatureName
   ```
3. Commit your changes:
   ```bash
   git commit -m "Add new feature"
   ```
4. Push to your branch:
   ```bash
   git push origin feature/FeatureName
   ```
5. Open a Pull Request

Follow existing code style and add tests where appropriate.

---

## Support

- Open an issue in the repository
- Contact the maintainer via the project community

---

## License

OSDev-IDE is released under the **[LICENSE NAME]**.  
Replace this with your chosen license (e.g., MIT, Apache 2.0).

---

## Acknowledgments

Thanks to the OSDev community and contributors who helped improve this IDE.
