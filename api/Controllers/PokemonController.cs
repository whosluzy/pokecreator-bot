using Microsoft.AspNetCore.Mvc;
using PokecreatorApi.Models;
using PokecreatorApi.Services;

namespace PokecreatorApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PokemonController(PkHexService pkHex) : ControllerBase
{
    [HttpGet("{species}/meta")]
    public IActionResult GetMeta(int species, [FromQuery] string game = "SV", [FromQuery] int form = 0)
    {
        try { return Ok(pkHex.GetMeta(game, species, form)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("{species}/encounters")]
    public IActionResult GetEncounters(int species, [FromQuery] string game = "SV", [FromQuery] int form = 0)
    {
        try { return Ok(pkHex.GetEncounters(game, species, form)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("{species}/moves")]
    public IActionResult GetMoves(int species, [FromQuery] string game = "SV", [FromQuery] int form = 0)
    {
        try { return Ok(pkHex.GetMoves(game, species, form)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("{species}/abilities")]
    public IActionResult GetAbilities(int species, [FromQuery] string game = "SV", [FromQuery] int form = 0)
    {
        try { return Ok(pkHex.GetAbilities(game, species, form)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("showdown")]
    public IActionResult Showdown([FromBody] PokemonConfig config)
    {
        try { return Ok(new { text = pkHex.ToShowdown(config) }); }
        catch (Exception ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("validate")]
    public IActionResult Validate([FromBody] PokemonConfig config)
    {
        try { return Ok(pkHex.Validate(config)); }
        catch (Exception ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("generate")]
    public IActionResult Generate([FromBody] PokemonConfig config)
    {
        try
        {
            var (data, fileName) = pkHex.Generate(config);
            return File(data, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
