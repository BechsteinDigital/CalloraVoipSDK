# Third-Party Notices

CalloraVoipSdk is licensed under the Apache License, Version 2.0 (see [LICENSE](./LICENSE)).

It redistributes the third-party components listed below. Each is used under a permissive
open-source license (MIT, BSD-3-Clause or Apache-2.0) that is compatible with redistribution
under Apache 2.0. No copyleft (GPL/LGPL/MPL/EPL) dependencies are used. The required copyright
notices and license texts are reproduced in this file.

> Only runtime components that ship with the SDK are listed. Build- and test-only packages
> (xUnit, Microsoft.NET.Test.Sdk, coverlet — all MIT/Apache-2.0) are not distributed with the SDK.

## Overview

| Component | Version | License | Copyright |
|---|---|---|---|
| BouncyCastle.Cryptography | 2.6.2 | MIT | © 2000–2025 The Legion of the Bouncy Castle Inc. |
| Concentus | 2.2.2 | BSD-3-Clause | © Xiph.Org Foundation, Skype Ltd., CSIRO, Microsoft Corp. et al. |
| DnsClient.NET | 1.8.0 | Apache-2.0 | © 2024 Michael Conrad |
| NAudio | 2.3.0 | MIT | © Mark Heath & NAudio contributors |
| NAudio.Core | 2.3.0 | MIT | © Mark Heath & NAudio contributors |
| PortAudioSharp2 | 1.0.6 | Apache-2.0 | © 2019 Benjamin N. Summerton; © Xiaomi/csukuangfj |
| Microsoft.Extensions.* | 8.0.x | MIT | © .NET Foundation and Contributors |

Projekt-URLs:
- BouncyCastle — https://www.bouncycastle.org
- Concentus — https://github.com/lostromb/concentus (portiert die Opus-Referenz von https://xiph.org/opus)
- DnsClient.NET — https://dnsclient.michaco.net
- NAudio — https://github.com/naudio/NAudio
- PortAudioSharp2 — https://github.com/csukuangfj/PortAudioSharp2 (bindet die native PortAudio-Bibliothek, MIT-artige PortAudio-Lizenz)
- Microsoft.Extensions.* — https://github.com/dotnet/runtime

---

## MIT License

The following components are licensed under the MIT License.

### BouncyCastle.Cryptography

```
Copyright (c) 2000-2025 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org).

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sub license, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### NAudio and NAudio.Core

```
Copyright © Mark Heath and NAudio contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### Microsoft.Extensions.* (DependencyInjection.Abstractions, Hosting.Abstractions, Logging.Abstractions, Options)

```
Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## BSD-3-Clause License

### Concentus

Concentus is a C# port of the Opus audio codec reference library. It is redistributed under the
same terms as the Opus reference library (BSD-3-Clause).

```
Copyright (c) by various holding parties, including (but not limited to):
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.
```

---

## Apache License 2.0

The following components are licensed under the Apache License, Version 2.0 — the same license
under which this SDK is distributed. The full license text is in [LICENSE](./LICENSE).

- **DnsClient.NET 1.8.0** — Copyright (c) 2024 Michael Conrad — https://dnsclient.michaco.net
- **PortAudioSharp2 1.0.6** — Copyright (c) 2019 Benjamin N. Summerton; Copyright (c) Xiaomi Corporation (csukuangfj) — https://github.com/csukuangfj/PortAudioSharp2

PortAudioSharp2 provides managed bindings to the native **PortAudio** library
(https://www.portaudio.com), which is distributed under the permissive MIT-style PortAudio license.

---

*This notice file is provided for attribution and license-compliance purposes and is not legal
advice. Before a commercial release, a final legal review of the third-party license obligations
is recommended.*
