// swift-tools-version: 5.9
import PackageDescription

// This package exists so the non-visual engine (playlist, guide, catch-up and recording logic)
// can be unit-tested with `swift test`. The apps themselves are built from project.yml.
let package = Package(
    name: "IPTVCore",
    platforms: [.iOS(.v16), .tvOS(.v16), .macOS(.v13)],
    products: [
        .library(name: "IPTVCore", targets: ["IPTVCore"])
    ],
    targets: [
        .target(
            name: "IPTVCore",
            path: "Core",
            linkerSettings: [.linkedLibrary("z")]
        ),
        .testTarget(
            name: "IPTVCoreTests",
            dependencies: ["IPTVCore"],
            path: "Tests/IPTVCoreTests"
        )
    ]
)
