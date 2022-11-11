﻿using PavlovVR_Rcon.Models.Replies;

namespace PavlovVR_Rcon.Models.Commands;

public class ShownametagsCommand : BaseCommand<ShownametagsReply>
{
    public ShownametagsCommand(bool showNameTags) : base("Shownametags")
    {
        this.addParameter(showNameTags ? "True" : "False");
    }
}
