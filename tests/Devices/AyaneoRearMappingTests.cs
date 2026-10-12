using LibHandheld.Families.Ayaneo;

namespace LibHandheld.Tests.Devices;

public sealed class AyaneoRearMappingTests
{
    [Theory]
    [InlineData("l4", CanonicalButtons.RearPaddle1, 0x82)]
    [InlineData("l5", CanonicalButtons.RearPaddle3, 0x80)]
    [InlineData("r4", CanonicalButtons.RearPaddle2, 0x83)]
    [InlineData("r5", CanonicalButtons.RearPaddle4, 0x81)]
    public void ModularOemChordsKeepPhysicalKeysAndCanonicalL4R4L5R5Order(string id,
        CanonicalButtons button, uint key)
    {
        foreach (var model in AyaneoModels.All.Where(item =>
                     item.Profile.Definition.Id is "ayaneo.3-8840u" or "ayaneo.3-hx370" or "ayaneo.next-2"))
        {
            var chord = model.Profile.KeyboardChords.Single(item => item.Id == id);
            Assert.Equal(button, chord.Button);
            Assert.Equal(new[] { key }, chord.Keys);
        }
    }
}
