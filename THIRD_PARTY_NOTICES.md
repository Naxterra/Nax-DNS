# Third-party notices

BetterDNS dynamically uses [Divert.Windows 3.0.0](https://www.nuget.org/packages/Divert.Windows/3.0.0) and [WinDivert 2.2.2](https://github.com/basil00/WinDivert) for Windows kernel packet interception.

Both components are distributed under the GNU Lesser General Public License, version 3. The installation keeps `Divert.Windows.dll`, `WinDivert.dll`, and `WinDivert64.sys` as separate replaceable files and installs the complete WinDivert license as `WinDivert-LICENSE.txt`. Their corresponding source is available from:

- https://github.com/gdlol/Divert.Windows
- https://github.com/basil00/WinDivert

BetterDNS itself remains licensed under the MIT License. No changes have been made to the WinDivert driver or Divert.Windows library.
