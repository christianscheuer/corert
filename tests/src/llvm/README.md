# ILC with LLVM backend
This is an early attempt at testing if we can use ILC to compile C# via LLVM for Mac (x86, eventually x86_64, arm64)

### Prerequisites

- `brew install llvm`
  <br/>(version 10)
  <br/>Location: `/usr/local/opt/llvm`

- An installed copy of the MacOSX10.13 SDK.
  <br/>This is needed since the 10.14 SDK no longer supports 32-bit linking.
  <br/>Location: `/Library/Developer/CommandLineTools/SDKs/MacOSX10.13.sdk`

