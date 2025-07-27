using System.Text;
using FluentAssertions;
using Jio.Core.Security;

namespace Jio.Core.Tests.Security;

public class IntegrityVerifierTests
{
    [Theory]
    [InlineData("sha1", "Hello, World!")]
    [InlineData("sha256", "Hello, World!")]
    [InlineData("sha384", "Hello, World!")]
    [InlineData("sha512", "Hello, World!")]
    public void ComputeIntegrity_Should_Generate_Valid_Hash(string algorithm, string content)
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var integrity = IntegrityVerifier.ComputeIntegrity(stream, algorithm);

        // Assert
        integrity.Should().StartWith($"{algorithm}-");
        integrity.Should().MatchRegex($"^{algorithm}-[A-Za-z0-9+/=]+$");
    }

    [Fact]
    public void VerifyIntegrity_Should_Return_True_For_Valid_Hash()
    {
        // Arrange
        var content = "Hello, World!";
        var stream1 = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var integrity = IntegrityVerifier.ComputeIntegrity(stream1, "sha512");
        
        var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = IntegrityVerifier.VerifyIntegrity(stream2, integrity);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyIntegrity_Should_Return_False_For_Invalid_Hash()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, World!"));
        var invalidIntegrity = "sha512-invalid_hash_value";

        // Act
        var result = IntegrityVerifier.VerifyIntegrity(stream, invalidIntegrity);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyIntegrity_Should_Return_False_For_Different_Content()
    {
        // Arrange
        var content1 = "Hello, World!";
        var content2 = "Goodbye, World!";
        var stream1 = new MemoryStream(Encoding.UTF8.GetBytes(content1));
        var integrity = IntegrityVerifier.ComputeIntegrity(stream1, "sha512");
        
        var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(content2));

        // Act
        var result = IntegrityVerifier.VerifyIntegrity(stream2, integrity);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyIntegrity_Should_Handle_Empty_Stream()
    {
        // Arrange
        var stream1 = new MemoryStream();
        var integrity = IntegrityVerifier.ComputeIntegrity(stream1, "sha256");
        
        var stream2 = new MemoryStream();

        // Act
        var result = IntegrityVerifier.VerifyIntegrity(stream2, integrity);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ComputeIntegrity_Should_Reset_Stream_Position()
    {
        // Arrange
        var content = "Hello, World!";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        stream.Seek(5, SeekOrigin.Begin);

        // Act
        IntegrityVerifier.ComputeIntegrity(stream, "sha256");

        // Assert
        stream.Position.Should().Be(0);
    }

    [Theory]
    [InlineData("sha256-n4bQgYhMfWWaL+qgxVrQFaO/TxsrC4Is0V1sFbDwCgg=", "test")]
    [InlineData("sha512-7iaw3Ur350mqGo7jwQrpkj9hiYB3Lkc/iBml1JQODbJ6wYX4oOHV+E+IvIh/1nsUNzLDBMxfqa2Ob1f1ACio/w==", "test")]
    public void VerifyIntegrity_Should_Handle_Known_Hashes(string expectedIntegrity, string content)
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = IntegrityVerifier.VerifyIntegrity(stream, expectedIntegrity);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ComputeIntegrity_Should_Throw_For_Unsupported_Algorithm()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));

        // Act & Assert
        var act = () => IntegrityVerifier.ComputeIntegrity(stream, "md5");
        act.Should().Throw<NotSupportedException>()
            .WithMessage("Unsupported algorithm: md5");
    }

    [Fact]
    public void VerifyIntegrity_Should_Return_False_For_Empty_Integrity()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));

        // Act
        var result = IntegrityVerifier.VerifyIntegrity(stream, "");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyIntegrity_Should_Return_False_For_Malformed_Integrity()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));

        // Act
        var result = IntegrityVerifier.VerifyIntegrity(stream, "invalid-format");

        // Assert
        result.Should().BeFalse();
    }
}