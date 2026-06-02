using Microsoft.AspNetCore.Mvc;
using PokecreatorApi.Services;

namespace PokecreatorApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameDataController(PkHexService pkHex) : ControllerBase
{
    [HttpGet("games")]
    public IActionResult GetGames() => Ok(pkHex.GetGames()); // returns List<GameEntry>

    [HttpGet("species")]
    public IActionResult GetSpecies([FromQuery] string game = "SV")
    {
        try { return Ok(pkHex.GetAllSpecies(game)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("natures")]
    public IActionResult GetNatures() => Ok(pkHex.GetNatures());

    [HttpGet("items")]
    public IActionResult GetItems([FromQuery] string game = "SV")
    {
        try { return Ok(pkHex.GetItems(game)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("balls")]
    public IActionResult GetBalls([FromQuery] string game = "SV", [FromQuery] int species = 0, [FromQuery] int form = 0)
    {
        try { return Ok(pkHex.GetBalls(game, species, form)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }
}
