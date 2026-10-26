# Third-party notices

Protocol calibration and portions of the framing, decoder, and LZ4 logic were adapted from [SkeeveAN/Aion-DPS-Meter](https://github.com/SkeeveAN/Aion-DPS-Meter) at commit `3aa8c732a539de8b8f09f4823f57d4ba1e6b4830`. The copied calibration/filter data files are `eu-opcodes.json`, `heal_skill_families.json`, and `rdps_watchlist.json`. That project is MIT licensed:

> MIT License
>
> Copyright (c) 2026 SkeeveTV
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

The application also references [SharpPcap 6.3.1](https://www.nuget.org/packages/SharpPcap/6.3.1) and [PacketDotNet 1.4.8](https://www.nuget.org/packages/PacketDotNet/1.4.8). Npcap is separately installed by the user and is not bundled here.

Package metadata declares SharpPcap 6.3.1 under MIT, copyright Tamir Gal, Chris Morgan and others; [source at the packaged revision](https://github.com/chmorgan/sharppcap/tree/bfedf297e7410ffcf44e05d14f7fbef304f20895). The MIT permission and warranty terms above also apply to that component with its own copyright holders.

PacketDotNet 1.4.8 is an unmodified dependency under [MPL-2.0](https://www.mozilla.org/MPL/2.0/), by Chris Morgan and contributors; [corresponding source](https://github.com/dotpcap/packetnet/tree/690707ce56d6e9c266daf6236c4f76ac5035334c). Its source files have not been incorporated into Thor Meter's own source.
